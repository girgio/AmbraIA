from fastapi import FastAPI
from pydantic import BaseModel
from typing import List, Dict, Any
from typing_extensions import TypedDict
import requests
from langgraph.graph import StateGraph, START, END

app = FastAPI()


# --- STATE MANAGEMENT ---
class AgentState(TypedDict):
    npc_goal: str
    raw_scene_data: Dict[str, Any]
    scene_summary: str
    current_plan: str
    generated_code: str
    compile_error: str


# --- PYDANTIC SCHEMAS ---
class OggettoVicino(BaseModel):
    name: str
    tag: str
    distance: float


class ReportScena(BaseModel):
    npc_goal: str
    npc_fame: float
    oggetti_vicini: List[OggettoVicino]


# --- NODO 1: SCENE ANALYZER ---
def scene_analyzer_node(state: AgentState) -> Dict[str, Any]:
    print("\n--- [LANGGRAPH] 1. Esecuzione: SCENE ANALYZER ---")
    npc_goal = state["npc_goal"]
    raw_scene = state["raw_scene_data"]

    system_prompt = (
        "Sei lo 'Scene Analyzer' di un NPC autonomo in Unity. Il tuo compito è leggere i dati grezzi della scena "
        "e l'obiettivo attuale dell'NPC. Devi eliminare gli oggetti irrilevanti e restituire "
        "un riassunto testuale brevissimo (massimo due righe), focalizzato SOLO su ciò che serve all'NPC per raggiungere il suo obiettivo.\n\n"
        "Regola: Sii conciso, parla in italiano. Non inventare oggetti."
    )

    oggetti_testo = "\n".join([
        f"- {obj['name']} (Tag: {obj['tag']}), Distanza: {obj['distance']:.2f}m"
        for obj in raw_scene["oggetti_vicini"]
    ])

    prompt_completo = f"Obiettivo NPC: \"{npc_goal}\"\nFame: {raw_scene['npc_fame']}\n\nScena Unity:\n{oggetti_testo}\n\nRiassunto filtrato:"

    try:
        response = requests.post(
            "http://localhost:11434/api/generate",
            json={"model": "llama3.1", "prompt": system_prompt + prompt_completo, "stream": False}
        )
        risposta_ia = response.json().get("response", "").strip()
    except Exception as e:
        risposta_ia = f"Errore Analyzer: {str(e)}"

    print(f"[ANALYZER] Risultato:\n{risposta_ia}")
    return {"scene_summary": risposta_ia}


# --- NODO 2: PLANNER (Nuovo!) ---
def planner_node(state: AgentState) -> Dict[str, Any]:
    print("\n--- [LANGGRAPH] 2. Esecuzione: PLANNER ---")
    npc_goal = state["npc_goal"]
    scene_summary = state["scene_summary"]

    system_prompt = (
        "Sei il 'Planner' strategico di un NPC in Unity. Il tuo compito è prendere l'obiettivo principale "
        "dell'NPC e il riassunto della scena circostante, e scomporre l'obiettivo in una sequenza ordinata "
        "di micro-compiti semplici in lingua italiana.\n\n"
        "Regole TASSATIVE:\n"
        "1. Restituisci le azioni come un elenco numerato (es. 1. Azione, 2. Azione).\n"
        "2. Ogni micro-compito deve essere semplice (es. 'Cammina verso il Pomodoro', 'Mangia il pomodoro').\n"
        "3. NON scrivere codice C#, solo logica in linguaggio naturale.\n"
        "4. Sii diretto, non aggiungere introduzioni come 'Ecco il piano:'."
    )

    prompt_completo = f"Obiettivo Finale: \"{npc_goal}\"\nVisione della Scena:\n{scene_summary}\n\nGenera la sequenza di azioni numerata:"

    try:
        response = requests.post(
            "http://localhost:11434/api/generate",
            json={"model": "llama3.1", "prompt": system_prompt + prompt_completo, "stream": False}
        )
        piano_generato = response.json().get("response", "").strip()
    except Exception as e:
        piano_generato = f"Errore Planner: {str(e)}"

    print(f"[PLANNER] Piano d'azione generato:\n{piano_generato}")
    return {"current_plan": piano_generato}


def builder_node(state: AgentState) -> Dict[str, Any]:
    print("\n--- [LANGGRAPH] 3. Esecuzione: BUILDER (Leggi della Fisica) ---")

    scene_summary = state["scene_summary"]
    current_plan = state["current_plan"]
    raw_data = state.get("raw_scene_data", {})

    oggetti_rilevati = [obj["name"] for obj in raw_data.get("oggetti_vicini", [])]
    lista_oggetti_stringa = ", ".join(
        [f"'{name}'" for name in oggetti_rilevati]) if oggetti_rilevati else "Nessun oggetto rilevato"

    system_prompt = f"""
Sei il 'Builder' di codice C# per Unity. Devi tradurre il piano d'azione in uno script C# chiamato 'AiAction'.

[REGOLE DI STRUTTURA E FISICA TASSATIVE]
1. Inserisci in cima: using UnityEngine; e using System.Collections;
2. Nome classe: 'AiAction' (eredita da MonoBehaviour).
3. In Start() avvia solo la coroutine: 'StartCoroutine(ExecutePlan());'.
4. In ExecutePlan() recupera subito il controller:
   NPCController controller = GetComponent<NPCController>();
   if (controller == null) {{ yield break; }}

[LEGGI DEL MONDO - VIETATO RIGIDAMENTE]
- NON creare MAI nuove funzioni o metodi personalizzati (NO 'void MoveToTarget', NO 'void Mangia'). Tutto deve stare dentro ExecutePlan().
- L'UNICO modo per spostarsi è usare la funzione del controller: controller.MoveToTarget("nome"); seguita da un yield return new WaitForSeconds(4f);
- È SEVERAMENTE VIETATO modificare la posizione di oggetti strutturali o grandi come 'Tavolo' o 'Pavimento'. Non puoi teletrasportare i mobili!

[OGGETTI REALI NELLA SCENA]
[{lista_oggetti_stringa}]

[GUIDA ALL'INTERAZIONE CREATIVA (SOLO DENTRO EXECUTEPLAN)]
Puoi usare il codice nativo Unity SOLO per interagire con piccoli oggetti bersaglio (come il pomodoro):

* RACCOGLIERE/PRENDERE IN MANO UN OGGETTO (Lo attacca all'NPC o lo fa fluttuare vicino):
  GameObject item = GameObject.Find("pomodoro");
  if (item != null) {{
      item.transform.position = transform.position + Vector3.up * 1.2f; // Fluttua davanti all'NPC
  }}
  yield return new WaitForSeconds(1f);

* MANGIARE/CONSUMARE UN OGGETTO:
  controller.ModifyStat("fame", -0.5f); // Riduce la fame
  GameObject item = GameObject.Find("pomodoro");
  if (item != null) {{ item.SetActive(false); }} // Il cibo sparisce perché è stato mangiato

* MODIFICARE DIMENSIONI O COLORE (Solo del piccolo oggetto interagito):
  GameObject item = GameObject.Find("pomodoro");
  if (item != null) {{ item.transform.localScale *= 0.8f; }}
"""

    prompt_completo = f"""
Visione della Scena: {scene_summary}
Piano da eseguire:
{current_plan}

Genera il codice C# pulito per la classe 'AiAction':
"""

    try:
        response = requests.post(
            "http://localhost:11434/api/generate",
            json={
                "model": "llama3.1",
                "prompt": system_prompt + prompt_completo,
                "stream": False,
                "options": {
                    "temperature": 0.0,  # Azzeriamo la fantasia per costringerla a seguire le regole
                    "top_p": 0.1
                }
            }
        )
        codice_csharp = response.json().get("response", "").strip()

        if "```csharp" in codice_csharp:
            codice_csharp = codice_csharp.split("```csharp")[1].split("```")[0].strip()
        elif "```" in codice_csharp:
            codice_csharp = codice_csharp.split("```")[1].split("```")[0].strip()

    except Exception as e:
        codice_csharp = f"// Errore Builder: {str(e)}"

    print(f"[BUILDER] Codice C# generato rispettando le leggi della fisica.")
    return {"generated_code": codice_csharp}

# --- NODO 4: INSPECTOR (Il Revisore) ---
def inspector_node(state: AgentState) -> Dict[str, Any]:
    print("\n--- [LANGGRAPH] 4. Esecuzione: INSPECTOR ---")

    # Se Unity ci ha mandato un errore, lo notifichiamo nel flusso
    if state.get("compile_error"):
        print(f"[INSPECTOR] Rilevato errore di compilazione: {state['compile_error']}")
        print("[INSPECTOR] Chiedo al Builder di correggere il codice...")

        # Iniettiamo l'errore nel piano corrente per forzare il Builder a leggerlo
        correzione_prompt = f"\n\n[ATTENZIONE - IL TUO CODICE PRECEDENTE È FALLITO]:\nErrore del compilatore Unity: {state['compile_error']}\nCorreggi questo errore strutturale nello script che stai per generare!"

        return {
            "current_plan": state["current_plan"] + correzione_prompt,
            "compile_error": ""  # Resettiamo l'errore così non andiamo in loop infinito
        }

    print("[INSPECTOR] Nessun errore rilevato. Codice approvato!")
    return state


# Funzione logica per decidere dove andare dopo l'Inspector
def decide_next_step(state: AgentState):
    # Se il Builder ha appena rigenerato il codice a causa di un errore,
    # facciamo passare di nuovo lo script dall'Inspector per sicurezza.
    # Se tutto è pulito, andiamo alla fine (END).
    if "ATTENZIONE" in state["current_plan"]:
        return "builder"
    return END

# --- COSTRUZIONE DEL GRAFO DI LANGGRAPH ---
# Aggiorna lo stato per ospitare il codice generato
class AgentState(TypedDict):
    npc_goal: str
    raw_scene_data: Dict[str, Any]
    scene_summary: str
    current_plan: str
    generated_code: str  # <--- Aggiunto!



workflow = StateGraph(AgentState)

# Registriamo tutti i nodi
workflow.add_node("scene_analyzer", scene_analyzer_node)
workflow.add_node("planner", planner_node)
workflow.add_node("builder", builder_node)
workflow.add_node("inspector", inspector_node) # <--- Nuovo nodo!

# Colleghiamo i nodi fisici
workflow.add_edge(START, "scene_analyzer")
workflow.add_edge("scene_analyzer", "planner")
workflow.add_edge("planner", "builder")
workflow.add_edge("builder", "inspector") # Il builder manda sempre all'inspector

# ROUTING CONDIZIONALE: L'Inspector decide se tornare al Builder o finire
workflow.add_conditional_edges(
    "inspector",
    decide_next_step,
    {
        "builder": "builder", # Torna a compilare
        END: END              # Tutto ok, manda a Unity
    }
)

compiled_graph = workflow.compile()


# --- AGGIORNAMENTO ENDPOINT FASTAPI ---
from pydantic import BaseModel
from typing import List, Optional


class ContextObject(BaseModel):
    name: str
    tag: str
    distance: float


class ReportScena(BaseModel):
    npc_goal: str
    npc_fame: float
    oggetti_vicini: List[ContextObject]
    compile_error: Optional[str] = None


@app.post("/npc/decide")
async def ricevi_scena_e_decidi(report: ReportScena):
    # Inizializziamo lo stato includendo l'eventuale errore che arriva da Unity
    stato_iniziale: AgentState = {
        "npc_goal": report.npc_goal,
        "raw_scene_data": report.model_dump(),
        "scene_summary": "",
        "current_plan": "",
        "generated_code": "",
        "compile_error": report.compile_error if report.compile_error else ""  # <--- Passiamo l'errore al grafo
    }

    stato_finale = compiled_graph.invoke(stato_iniziale)

    return {
        "status": "success",
        "plan": stato_finale["current_plan"],
        "code": stato_finale["generated_code"]
    }


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=8000)