from fastapi import FastAPI
from pydantic import BaseModel
from typing import List, Dict, Any, Optional
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
class ContextObject(BaseModel):
    name: str
    tag: str
    distance: float
    available_action: str

class ReportScena(BaseModel):
    npc_goal: str
    npc_fame: float
    oggetti_vicini: List[ContextObject]
    compile_error: Optional[str] = None

# --- NODO 1: SCENE ANALYZER (Blindato) ---
def scene_analyzer_node(state: AgentState) -> Dict[str, Any]:
    print("\n--- [LANGGRAPH] 1. Esecuzione: SCENE ANALYZER ---")
    npc_goal = state["npc_goal"]
    raw_scene = state["raw_scene_data"]

    system_prompt = (
        "Sei lo 'Scene Analyzer' di un NPC in un VIDEOGIOCO 3D SIMULATO (Unity). Non è il mondo reale.\n"
        "Il tuo compito è leggere i dati grezzi della scena e restituire un riassunto testuale brevissimo della scena, non devi dare azioni descrivi solo la scena(massimo tre righe).\n"
        "Regola TASSATIVA: Basati SOLO ed ESCLUSIVAMENTE sugli oggetti presenti nella lista grezza in input. "
        "È SEVERAMENTE VIETATO inventare oggetti, nomi o azioni non presenti. Sii un puro filtro dati. Parla in italiano."
    )

    oggetti_testo = "\n".join([
        f"- {obj['name']} (Tag: {obj['tag']}), Distanza: {obj['distance']:.2f}m, Azione Possibile: '{obj['available_action']}'"
        for obj in raw_scene["oggetti_vicini"]
    ])

    prompt_completo = f"Obiettivo NPC: \"{npc_goal}\"\nFame: {raw_scene['npc_fame']}\n\nScena Unity:\n{oggetti_testo}\n\nRiassunto filtrato:"

    try:
        response = requests.post(
            "http://localhost:11434/api/generate",
            json={
                "model": "llama3.1",
                "prompt": system_prompt + prompt_completo,
                "stream": False,
                "options": {
                    "temperature": 0.0,  # <--- AZZERATO: Impedisce l'allucinazione di oggetti
                    "top_p": 0.1
                }
            }
        )
        risposta_ia = response.json().get("response", "").strip()
    except Exception as e:
        risposta_ia = f"Errore Analyzer: {str(e)}"

    print(f"[ANALYZER] Risultato:\n{risposta_ia}")
    return {"scene_summary": risposta_ia}

# --- NODO 2: PLANNER (Blindato) ---
def planner_node(state: AgentState) -> Dict[str, Any]:
    print("\n--- [LANGGRAPH] 2. Esecuzione: PLANNER ---")
    npc_goal = state["npc_goal"]
    scene_summary = state["scene_summary"]

    system_prompt = (
        "Sei il 'Planner' strategico di un personaggio virtuale dentro un VIDEOGIOCO 3D (Unity). "
        "Scomponi l'obiettivo in azioni logiche sequenziali, basati su gli oggetti interagibili.\n\n"
        "Regole TASSATIVE:\n"
        "1. Restituisci le azioni come elenco numerato.\n"
        "2. Puoi pianificare di usare un oggetto SOLO se l'oggetto è esplicitamente menzionato nella Visione della Scena.\n"
        "3. NON scrivere codice, solo logica in italiano."
    )

    prompt_completo = f"Obiettivo Finale: \"{npc_goal}\"\nVisione della Scena:\n{scene_summary}\n\nGenera il piano:"

    try:
        response = requests.post(
            "http://localhost:11434/api/generate",
            json={
                "model": "llama3.1",
                "prompt": system_prompt + prompt_completo,
                "stream": False,
                "options": {
                    "temperature": 0.0,  # <--- AZZERATO: Previene risposte fuori tracciato
                    "top_p": 0.1
                }
            }
        )
        piano_generato = response.json().get("response", "").strip()
    except Exception as e:
        piano_generato = f"Errore Planner: {str(e)}"

    print(f"[PLANNER] Piano d'azione generato:\n{piano_generato}")
    return {"current_plan": piano_generato}

# --- NODO 3: BUILDER (Super Protetto contro ogni allucinazione) ---
def builder_node(state: AgentState) -> Dict[str, Any]:
    print("\n--- [LANGGRAPH] 3. Esecuzione: BUILDER (Modalità Restrittiva) ---")

    scene_summary = state["scene_summary"]
    current_plan = state["current_plan"]
    raw_data = state.get("raw_scene_data", {})

    oggetti_rilevati = [f"{obj['name']} (Azione valida: '{obj['available_action']}')" for obj in raw_data.get("oggetti_vicini", [])]
    lista_oggetti_stringa = "\n".join(oggetti_rilevati) if oggetti_rilevati else "Nessun oggetto rilevato"

    system_prompt = f"""
Sei il 'Builder' di codice C# per Unity. Devi tradurre il piano d'azione in uno script C# chiamato 'AiAction'.

[REGOLE DI STRUTTURA TASSATIVE]
1. Inserisci in cima: using UnityEngine; e using System.Collections;
2. Nome classe: 'AiAction' (eredita da MonoBehaviour).
3. In Start() avvia la coroutine: 'StartCoroutine(ExecutePlan());'.
4. All'inizio di ExecutePlan() devi inserire REQUISITO TASSATIVO questa esatta dichiarazione per il controller:
   NPCController controller = GetComponent<NPCController>();
   if (controller == null) {{ yield break; }}

[LIMITAZIONI SUL CODICE - DIVIETO ASSOLUTO DI INVENTARE]
La classe del controller si chiama 'NPCController'. È SEVERAMENTE VIETATO inventare altri nomi come 'BPS_Controller' o 'AiController'.
Non puoi usare GameObject.Find o SetActive. 
Hai a disposizione SOLO ED ESCLUSIVAMENTE queste due funzioni del controller:
- controller.MoveToTarget("NOME_OGGETTO");
- controller.InteractWith("NOME_OGGETTO", "NOME_AZIONE");

Se sei troppo lontano da un oggetto avvicinati prima di interagire.

[IL CICLO DI AZIONE OBBLIGATORIO]
Quando interagisci, usa ESATTAMENTE questa struttura in 3 step dentro ExecutePlan():
controller.MoveToTarget("NOME_OGGETTO");
yield return new WaitForSeconds(3.5f); // ATTESA OBBLIGATORIA PER CAMMINARE
controller.InteractWith("NOME_OGGETTO", "NOME_AZIONE");

[OGGETTI E AZIONI VALIDE ORA NELLA SCENA]
{lista_oggetti_stringa}

(Alla fine del metodo ExecutePlan, scrivi sempre: Destroy(this); yield break;)
"""

    prompt_completo = f"""
Visione della Scena: {scene_summary}
Piano da eseguire:
{current_plan}

Genera SOLO il codice C# pulito per la classe 'AiAction':
"""

    try:
        response = requests.post(
            "http://localhost:11434/api/generate",
            json={
                "model": "llama3.1",
                "prompt": system_prompt + prompt_completo,
                "stream": False,
                "options": {
                    "temperature": 0.0,
                    "top_p": 0.1
                }
            }
        )
        codice_csharp = response.json().get("response", "").strip()

        if "```csharp" in codice_csharp:
            codice_csharp = codice_csharp.split("```csharp")[1].split("```")[0].strip()
        elif "```" in codice_csharp:
            codice_csharp = codice_csharp.split("```")[1].split("```")[0].strip()

        # ---------------------------------------------------------------------------------
        # TRUCCO DI SICUREZZA TOTALE: Intercettiamo TUTTE le varianti inventate da Llama
        nomi_allucinati = ["BPS_Controller", "AiController", "AIController", "NpcController", "NPC_Controller"]
        for nome_falso in nomi_allucinati:
            if nome_falso in codice_csharp:
                print(f"[BUILDER - WARNING] Intercettata allucinazione '{nome_falso}'. Sostituisco forzatamente con 'NPCController'.")
                codice_csharp = codice_csharp.replace(nome_falso, "NPCController")
        # ---------------------------------------------------------------------------------

    except Exception as e:
        codice_csharp = f"// Errore Builder: {str(e)}"

    print(f"[BUILDER] Codice C# generato con successo.")
    return {"generated_code": codice_csharp}

# --- NODO 4: INSPECTOR ---
def inspector_node(state: AgentState) -> Dict[str, Any]:
    print("\n--- [LANGGRAPH] 4. Esecuzione: INSPECTOR ---")
    if state.get("compile_error"):
        print(f"[INSPECTOR] Rilevato errore di compilazione: {state['compile_error']}")
        correzione_prompt = f"\n\n[ATTENZIONE - IL CODICE È FALLITO]:\nErrore Unity: {state['compile_error']}\nCorreggi lo script attenendoti RIGOROSAMENTE alle funzioni consentite."
        return {
            "current_plan": state["current_plan"] + correzione_prompt,
            "compile_error": ""
        }
    print("[INSPECTOR] Nessun errore rilevato. Codice approvato!")
    return state

# --- ROUTING E GRAFO ---
def decide_next_step(state: AgentState):
    if "ATTENZIONE" in state["current_plan"]:
        return "builder"
    return END

workflow = StateGraph(AgentState)
workflow.add_node("scene_analyzer", scene_analyzer_node)
workflow.add_node("planner", planner_node)
workflow.add_node("builder", builder_node)
workflow.add_node("inspector", inspector_node)

workflow.add_edge(START, "scene_analyzer")
workflow.add_edge("scene_analyzer", "planner")
workflow.add_edge("planner", "builder")
workflow.add_edge("builder", "inspector")
workflow.add_conditional_edges("inspector", decide_next_step, {"builder": "builder", END: END})

compiled_graph = workflow.compile()

# --- API ENDPOINT ---
@app.post("/npc/decide")
async def ricevi_scena_e_decidi(report: ReportScena):
    stato_iniziale: AgentState = {
        "npc_goal": report.npc_goal,
        "raw_scene_data": report.model_dump(),
        "scene_summary": "",
        "current_plan": "",
        "generated_code": "",
        "compile_error": report.compile_error if report.compile_error else ""
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