import json, os, random, math
from pathlib import Path
from typing import Annotated, TypedDict, Literal
import operator

from langchain_ollama import ChatOllama
from langchain_core.messages import AnyMessage, HumanMessage, SystemMessage
from langgraph.checkpoint.memory import MemorySaver
from langgraph.graph import StateGraph, START, END
from flask import Flask, request, jsonify
from dotenv import load_dotenv

load_dotenv()

# ─── Q-Table persistente su disco ───────────────────────────────────────────
Q_TABLE_PATH = Path("q_table.json")
ALPHA = 0.3   # learning rate
GAMMA = 0.9   # discount factor
ACTIONS = ["move", "eat_food", "cook", "sit_down", "do_nothing"]

def load_q_table() -> dict:
    if Q_TABLE_PATH.exists():
        return json.loads(Q_TABLE_PATH.read_text())
    return {}

def save_q_table(q: dict):
    Q_TABLE_PATH.write_text(json.dumps(q, indent=2))

def get_state_key(tipo_oggetto: str, distanza: float,
                  nome_oggetto: str = "",
                  ultima_azione: str = "",
                  fame: float = 0.0) -> str:
    """Discretizza lo stato in una chiave per la Q-table."""
    # Bucket distanza
    if distanza < 2.0:
        dist_bucket = "vicino"
    elif distanza < 6.0:
        dist_bucket = "medio"
    else:
        dist_bucket = "lontano"



    # Bucket fame
    if fame < 0.3:
        fame_bucket = "sazio"
    elif fame < 0.7:
        fame_bucket = "affamato"
    else:
        fame_bucket = "molto_affamato"

    return f"{tipo_oggetto}_{dist_bucket}_{fame_bucket}"

def bellman_update(q_table: dict, state_key: str, action: str,
                   reward: float, next_state_key: str):
    """Aggiornamento Q-Learning classico: Q(s,a) += α * (r + γ*max_Q(s') - Q(s,a))"""
    if state_key not in q_table:
        q_table[state_key] = {a: 0.0 for a in ACTIONS}
    if next_state_key not in q_table:
        q_table[next_state_key] = {a: 0.0 for a in ACTIONS}

    old_q = q_table[state_key].get(action, 0.0)
    max_next = max(q_table[next_state_key].values())
    new_q = old_q + ALPHA * (reward + GAMMA * max_next - old_q)
    q_table[state_key][action] = round(new_q, 4)

# ─── Stato LangGraph ─────────────────────────────────────────────────────────
class NPCState(TypedDict):
    messages: Annotated[list[AnyMessage], operator.add]
    distanza: float
    tipo_oggetto: str
    nome_oggetto: str
    descrizione: str
    ultimo_reward: float
    state_key: str
    prev_state_key: str
    prev_action: str
    q_hints: dict
    llm_q_values: dict
    azione_scelta: str
    retry_count: int
    fame: float             # 0.0 = sazio, 1.0 = molto affamato
    valore_nutritivo: float # valore nutritivo dell'oggetto corrente

# ─── Modello ─────────────────────────────────────────────────────────────────
model = ChatOllama(model="llama3.1", temperature=0.2)

# ─── Nodi del grafo ──────────────────────────────────────────────────────────

def prepare_context(state: NPCState) -> dict:
    """Carica Q-table, applica Bellman se c'è un reward precedente, prepara hint."""
    q_table = load_q_table()

    state_key = get_state_key(
        tipo_oggetto  = state["tipo_oggetto"],
        distanza      = state["distanza"],
        fame          = state.get("fame", 0.0)
    )

    # Aggiornamento Bellman se abbiamo dati del turno precedente
    prev_key    = state.get("prev_state_key", "")
    prev_action = state.get("prev_action", "")
    reward      = state.get("ultimo_reward", 0.0)

    if prev_key and prev_action:
        bellman_update(q_table, prev_key, prev_action, reward, state_key)
        save_q_table(q_table)

    q_hints = q_table.get(state_key, {a: 0.0 for a in ACTIONS})

    return {"state_key": state_key, "q_hints": q_hints}


def llm_call(state: NPCState) -> dict:
    """Chiede all'LLM di rifinire i Q-values in base al contesto, fame e valore nutritivo."""
    q_hints          = state.get("q_hints", {a: 0.0 for a in ACTIONS})
    distanza         = state["distanza"]
    descrizione      = state.get("descrizione", "oggetto sconosciuto")
    fame             = state.get("fame", 0.0)
    valore_nutritivo = state.get("valore_nutritivo", 0.3)

    sys_msg = SystemMessage(content=(
        "Sei il cervello decisionale di un NPC. Rispondi SOLO con JSON valido, "
        "nessun commento, nessun testo extra.\n\n"
        f"SITUAZIONE: '{descrizione}', distanza {distanza:.1f}m.\n"
        f"FAME ATTUALE: {fame:.2f} (0.0=sazio, 1.0=molto affamato)\n"
        f"VALORE NUTRITIVO OGGETTO: {valore_nutritivo:.2f} (0.0=nullo, 1.0=molto nutritivo)\n"
        f"Q-VALUES APPRESI (baseline):\n{json.dumps(q_hints)}\n\n"
        "REGOLE:\n"
        "- Se distanza > 2.0 → 'move' deve avere il valore più alto\n"
        "- Se distanza < 2.0 → 'move' deve avere valore negativo\n"
        "- Se fame > 0.7 e oggetto è Cibo → 'eat_food' deve avere valore alto\n"
        "- Se fame < 0.3 → 'eat_food' deve avere valore basso o negativo\n"
        "- Considera il valore nutritivo: cibo più nutritivo vale di più se hai fame\n"
        "- Aggiusta gli altri valori in base a cosa ha senso fare con l'oggetto\n"
        "- Valori nell'intervallo [-1.0, 1.0]\n\n"
        'Formato risposta: {"q_values": {"move": 0.0, "eat_food": 0.0, '
        '"cook": 0.0, "sit_down": 0.0, "do_nothing": 0.0}}'
    ))

    response = model.invoke([sys_msg] + state["messages"])
    return {"messages": [response], "retry_count": state.get("retry_count", 0)}


def validate_json(state: NPCState) -> dict:
    """Parsa la risposta dell'LLM con fallback sicuro."""
    last_msg    = state["messages"][-1]
    retry_count = state.get("retry_count", 0)

    try:
        text  = last_msg.content.strip()
        start = text.find("{")
        end   = text.rfind("}") + 1
        if start == -1:
            raise ValueError("Nessun JSON trovato")
        data   = json.loads(text[start:end])
        q_vals = data["q_values"]

        if not all(k in ACTIONS for k in q_vals):
            raise ValueError("Chiavi non valide")

        return {"llm_q_values": q_vals, "retry_count": 0}

    except Exception as e:
        print(f"[validate_json] Errore parsing (tentativo {retry_count}): {e}")
        if retry_count < 2:
            return {"retry_count": retry_count + 1}
        print("[validate_json] Fallback su Q-table.")
        return {"llm_q_values": state.get("q_hints", {a: 0.0 for a in ACTIONS}),
                "retry_count": 0}


def choose_action(state: NPCState) -> dict:
    """Seleziona l'azione finale combinando Q-table e suggerimento LLM."""
    q_llm   = state.get("llm_q_values", {})
    q_hints = state.get("q_hints", {})

    # Media pesata: 70% Q-table appresa, 30% intuizione LLM
    q_combined = {}
    for a in ACTIONS:
        q_combined[a] = 0.7 * q_hints.get(a, 0.0) + 0.3 * q_llm.get(a, 0.0)

    azione = roulette_wheel_selection(q_combined, temperature=0.5)

    print(f"[choose_action] Q-combinati: {q_combined}")
    print(f"[choose_action] Azione scelta: {azione}")

    return {
        "azione_scelta":   azione,
        "prev_action":     azione,
        "prev_state_key":  state["state_key"],
    }


def roulette_wheel_selection(q_values: dict, temperature: float = 1.0) -> str:
    """Softmax selection: più temperatura = più esplorazione."""
    actions  = list(q_values.keys())
    logits   = [q_values[a] / max(temperature, 1e-6) for a in actions]
    max_l    = max(logits)
    exp_vals = [math.exp(v - max_l) for v in logits]
    total    = sum(exp_vals)
    probs    = [e / total for e in exp_vals]
    return random.choices(actions, weights=probs, k=1)[0]


# ─── Edge condizionale ───────────────────────────────────────────────────────
def should_retry(state: NPCState) -> Literal["llm_call", "choose_action"]:
    if state.get("retry_count", 0) > 0:
        return "llm_call"
    return "choose_action"


# ─── Costruzione grafo ───────────────────────────────────────────────────────
memory = MemorySaver()

builder = StateGraph(NPCState)
builder.add_node("prepare_context", prepare_context)
builder.add_node("llm_call",        llm_call)
builder.add_node("validate_json",   validate_json)
builder.add_node("choose_action",   choose_action)

builder.add_edge(START, "prepare_context")
builder.add_edge("prepare_context", "llm_call")
builder.add_edge("llm_call",        "validate_json")
builder.add_conditional_edges("validate_json", should_retry,
                               {"llm_call":      "llm_call",
                                "choose_action": "choose_action"})
builder.add_edge("choose_action", END)

agent = builder.compile(checkpointer=memory)

# ─── Flask API ───────────────────────────────────────────────────────────────
app_flask = Flask(__name__)

@app_flask.route('/npc/decide', methods=['POST'])
def decide_azione():
    data             = request.json
    distanza         = round(float(data.get("distanza", 99.0)), 2)
    reward_reale     = float(data.get("ultimoReward", 0.0))
    nome_oggetto     = str(data.get("nomeOggetto", "oggetto ignoto"))
    tipo_oggetto     = str(data.get("tipoOggetto", "generico"))
    descrizione      = str(data.get("descrizione", nome_oggetto))
    fame             = float(data.get("fame", 0.0))
    valore_nutritivo = float(data.get("valoreNutritivo", 0.0))

    config = {"configurable": {"thread_id": "NPC_Chef_01"}}

    prev_state     = agent.get_state(config)
    prev_action    = ""
    prev_state_key = ""
    if prev_state.values:
        prev_action    = prev_state.values.get("azione_scelta", "")
        prev_state_key = prev_state.values.get("state_key", "")

    final_state = agent.invoke({
        "messages":         [HumanMessage(content=descrizione)],
        "distanza":         distanza,
        "tipo_oggetto":     tipo_oggetto,
        "nome_oggetto":     nome_oggetto,
        "descrizione":      descrizione,
        "ultimo_reward":    reward_reale,
        "prev_action":      prev_action,
        "prev_state_key":   prev_state_key,
        "retry_count":      0,
        "fame":             fame,
        "valore_nutritivo": valore_nutritivo,
    }, config=config)

    azione     = final_state.get("azione_scelta", "do_nothing")
    q_combined = final_state.get("llm_q_values", {})
    q_previsto = q_combined.get(azione, 0.0)

    # Calcola nuova fame dopo l'azione
    nuova_fame = fame
    if azione == "eat_food":
        nuova_fame = max(0.0, fame - valore_nutritivo)

    return jsonify({
        "azione":          azione,
        "reward_previsto": q_previsto,
        "q_values":        q_combined,
        "nuovaFame":       round(nuova_fame, 4),
    })

if __name__ == "__main__":
    app_flask.run(host='0.0.0.0', port=5000)
