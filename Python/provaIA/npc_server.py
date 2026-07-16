"""
NPC AI Server - Architettura basata su Park et al. 2023 ("Generative Agents")
e survey "LLM as an Interactive NPC" (2025).

Componenti LangGraph: RETRIEVAL -> PLANNER -> BUILDER -> INSPECTOR -> END
Memory Stream persistito su JSON con embedding via nomic-embed-text.
Reflector periodico per sintetizzare riflessioni di alto livello.
"""

import json
import os
import time
import uuid
import logging
import re
from typing import List, Dict, Any, Optional

import numpy as np
import requests
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from typing_extensions import TypedDict

from langgraph.graph import StateGraph, START, END

logging.basicConfig(level=logging.INFO, format="[%(levelname)s] %(message)s")
logger = logging.getLogger(__name__)

app = FastAPI(title="NPC AI Server")

OLLAMA_URL = "http://localhost:11434"
OLLAMA_MODEL = "llama3.1"
EMBED_MODEL = "nomic-embed-text"
MAX_RETRIES = 3
MEMORY_FILE = "memory_stream.json"
REFLECTION_INTERVAL = 5
MEMORY_TOP_N = 10
ALPHA_RECENCY = 0.3
ALPHA_IMPORTANCE = 0.3
ALPHA_RELEVANCE = 0.4
DECAY_LAMBDA = 0.99


class OllamaClient:
    @staticmethod
    def generate(system_prompt: str, user_prompt: str, temperature: float = 0.0) -> str:
        full_prompt = system_prompt.strip() + "\n\n" + user_prompt.strip()
        response = requests.post(
            f"{OLLAMA_URL}/api/generate",
            json={
                "model": OLLAMA_MODEL,
                "prompt": full_prompt,
                "stream": False,
                "options": {
                    "temperature": temperature,
                    "top_p": 0.1 if temperature == 0.0 else 0.9,
                },
            },
            timeout=120,
        )
        response.raise_for_status()
        result = response.json().get("response", "").strip()
        if not result:
            raise ValueError("Ollama ha restituito una risposta vuota.")
        return result

    @staticmethod
    def embed(text: str) -> List[float]:
        response = requests.post(
            f"{OLLAMA_URL}/api/embeddings",
            json={"model": EMBED_MODEL, "prompt": text},
            timeout=60,
        )
        response.raise_for_status()
        return response.json()["embedding"]


ollama = OllamaClient()


def load_memory_stream() -> List[Dict[str, Any]]:
    if os.path.exists(MEMORY_FILE):
        with open(MEMORY_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    return []


def save_memory_stream(memories: List[Dict[str, Any]]):
    with open(MEMORY_FILE, "w", encoding="utf-8") as f:
        json.dump(memories, f, ensure_ascii=False, indent=2)


def add_memory(content: str, mem_type: str, importance: int, embedding: List[float]):
    memories = load_memory_stream()
    memory = {
        "id": f"mem_{uuid.uuid4().hex[:8]}",
        "content": content,
        "timestamp": time.time(),
        "importance": importance,
        "type": mem_type,
        "embedding": embedding,
    }
    memories.append(memory)
    save_memory_stream(memories)
    logger.info(f"[MEMORY] Aggiunto ricordo '{content[:60]}...' (type={mem_type}, importance={importance})")
    return memory


def score_importance(action_description: str) -> int:
    system_prompt = (
        "Valuta l'importanza di questa esperienza per un NPC in un videogioco "
        "su una scala da 1 (irrilevante) a 10 (fondamentale).\n"
        "Considera: impatto sullo stato fisico, novita', utilita' per decisioni future.\n"
        "Rispondi SOLO con un numero intero."
    )
    prompt = f"Esperienza: '{action_description}'"
    try:
        result = ollama.generate(system_prompt, prompt, temperature=0.0)
        importance = int(result.strip())
        return max(1, min(10, importance))
    except Exception:
        return 5


def cosine_similarity(a: List[float], b: List[float]) -> float:
    a_np = np.array(a)
    b_np = np.array(b)
    norm_a = np.linalg.norm(a_np)
    norm_b = np.linalg.norm(b_np)
    if norm_a == 0 or norm_b == 0:
        return 0.0
    return float(np.dot(a_np, b_np) / (norm_a * norm_b))


def retrieve_relevant_memories(query_text: str, top_n: int = MEMORY_TOP_N) -> List[Dict[str, Any]]:
    memories = load_memory_stream()
    if not memories:
        return []

    query_embedding = ollama.embed(query_text)
    now = time.time()

    scored = []
    for mem in memories:
        delta_hours = (now - mem["timestamp"]) / 3600.0
        recency = 1.0 / (1.0 + DECAY_LAMBDA * delta_hours)
        importance = mem["importance"] / 10.0
        relevance = max(0.0, cosine_similarity(mem["embedding"], query_embedding))
        total_score = (
            ALPHA_RECENCY * recency
            + ALPHA_IMPORTANCE * importance
            + ALPHA_RELEVANCE * relevance
        )
        scored.append((total_score, mem))

    scored.sort(key=lambda x: x[0], reverse=True)
    top_memories = [mem for _, mem in scored[:top_n]]
    logger.info(f"[RETRIEVAL] Recuperati {len(top_memories)} ricordi su {len(memories)} totali.")
    return top_memories


class ContextObject(BaseModel):
    name: str
    tag: str
    distance: float
    available_action: str
    effect: str = "none"


class ReportScena(BaseModel):
    npc_goal: str
    npc_fame: float
    npc_energy: float
    oggetti_vicini: List[ContextObject]
    last_actions: Optional[List[str]] = []
    npc_reflections: Optional[List[str]] = []
    compile_error: Optional[str] = None


class ActionDoneReport(BaseModel):
    action_description: str
    npc_fame: float
    npc_energy: float


class AgentState(TypedDict):
    npc_goal: str
    npc_fame: float
    npc_energy: float
    last_actions: List[str]
    npc_reflections: List[str]
    scene_summary: str
    retrieved_memories: List[Dict[str, Any]]
    valid_object_names: List[str]
    valid_actions_map: Dict[str, str]
    valid_distances: Dict[str, float]
    valid_effects: Dict[str, str]
    current_plan: str
    generated_code: str
    has_compile_error: bool
    compile_error: str
    retry_count: int


def infer_effect(action_name: str, obj_name: str, tag: str, explicit_effect: str) -> str:
    if explicit_effect and explicit_effect != "none":
        return explicit_effect
    action_lower = action_name.lower()
    obj_lower = obj_name.lower()
    tag_lower = tag.lower()
    riduce_fame = any(w in action_lower for w in ["mangia", "eat", "bevi", "drink", "divora", "consuma"])
    riduce_stanchezza = any(w in action_lower for w in ["siediti", "sit", "dormi", "sleep", "riposa", "rest", "sdraiati"])
    if any(w in obj_lower for w in ["cibo", "food", "piatto", "mela", "pane", "acqua", "bevanda"]):
        riduce_fame = True
    if any(w in obj_lower for w in ["sedia", "chair", "letto", "bed", "panchina", "bench", "divano", "sofa", "poltrona"]):
        riduce_stanchezza = True
    if tag_lower in ["food", "cibo"]:
        riduce_fame = True
    if tag_lower in ["furniture", "arredo", "bed", "chair"]:
        riduce_stanchezza = True
    if riduce_fame and riduce_stanchezza:
        return "both"
    elif riduce_fame:
        return "hunger"
    elif riduce_stanchezza:
        return "energy"
    return "none"


def effect_label(effect: str) -> str:
    if effect == "hunger":
        return "riduce la fame"
    elif effect == "energy":
        return "riduce la stanchezza"
    elif effect == "both":
        return "riduce sia fame che stanchezza"
    return "nessun effetto noto"


def build_scene_summary(report: ReportScena) -> str:
    lines = [f"Obiettivo NPC: \"{report.npc_goal}\""]
    lines.append(f"Stato NPC - Fame: {report.npc_fame:.1f}/1.0  |  Stanchezza: {report.npc_energy:.1f}/1.0")

    if report.npc_reflections:
        lines.append("\nRiflessioni di lungo termine sul proprio comportamento:")
        for r in report.npc_reflections[-3:]:
            lines.append(f"  - {r}")

    if report.last_actions:
        lines.append(f"\nAzioni recenti gia' eseguite: {', '.join(report.last_actions[-5:])}")
    else:
        lines.append("\nAzioni recenti: nessuna")

    interagibili = [o for o in report.oggetti_vicini if o.available_action.lower() != "nessuna"]
    non_interagibili = [o for o in report.oggetti_vicini if o.available_action.lower() == "nessuna"]

    lines.append("\nOggetti con cui puoi INTERAGIRE:")
    if interagibili:
        for obj in interagibili:
            eff = infer_effect(obj.available_action, obj.name, obj.tag, obj.effect)
            lines.append(
                f"  - {obj.name} (Tag: {obj.tag}) | Distanza: {obj.distance:.1f}m "
                f"| Azione: \"{obj.available_action}\" | Effetto: {effect_label(eff)}"
            )
    else:
        lines.append("  (nessuno)")

    if non_interagibili:
        lines.append("\nAltri oggetti nella scena (arredo, NON interagibili):")
        for obj in non_interagibili:
            lines.append(f"  - {obj.name} | Distanza: {obj.distance:.1f}m")

    return "\n".join(lines)


def build_retrieval_query(report: ReportScena) -> str:
    nomi = [o.name for o in report.oggetti_vicini]
    return (
        f"Obiettivo: {report.npc_goal}. "
        f"Stato: fame={report.npc_fame:.1f}, stanchezza={report.npc_energy:.1f}. "
        f"Oggetti vicini: {', '.join(nomi)}. "
        f"Azioni recenti: {', '.join(report.last_actions[-5:]) if report.last_actions else 'nessuna'}."
    )


def validate_generated_code(code: str, valid_names: List[str]) -> List[str]:
    pattern = r'(?:MoveToTargetAndWait|InteractWithAndWait)\s*\(\s*"([^"]+)"'
    used_names = re.findall(pattern, code)
    return [n for n in used_names if n not in valid_names]


def find_placeholder_issues(code: str) -> List[str]:
    issues = []
    if re.search(r'(?:if|while)\s*\(\s*/\*.*?\*/\s*\)', code, re.DOTALL):
        issues.append("Condizione if/while con commento placeholder invece di codice reale.")
    if re.search(r'(?:if|while)\s*\(\s*\)', code):
        issues.append("Condizione if/while vuota.")
    for phrase in ["TODO", "da implementare", "inserisci qui", "pseudo-codice", "condizione per determinare"]:
        if phrase.lower() in code.lower():
            issues.append(f"Testo placeholder non risolto: '{phrase}'.")
    return issues


HALLUCINATED_CONTROLLER_NAMES = [
    "BPS_Controller", "AiController", "AIController",
    "NpcController", "NPC_Controller", "NPCcontroller",
]


def fix_controller_name(code: str) -> str:
    for bad_name in HALLUCINATED_CONTROLLER_NAMES:
        if bad_name in code:
            logger.warning(f"[BUILDER] Nome controller allucinato corretto: '{bad_name}' -> 'NPCController'")
            code = code.replace(bad_name, "NPCController")
    return code


def strip_markdown_fences(code: str) -> str:
    if "```csharp" in code:
        code = code.split("```csharp")[1].split("```")[0]
    elif "```" in code:
        code = code.split("```")[1].split("```")[0]
    return code.strip()


# ==============================================================================
# NODI LANGGRAPH
# ==============================================================================

def retrieval_node(state: AgentState) -> Dict[str, Any]:
    logger.info("--- [LANGRAPH] 1. RETRIEVAL ---")
    memories = retrieve_relevant_memories(state["scene_summary"])
    return {"retrieved_memories": memories}


def planner_node(state: AgentState) -> Dict[str, Any]:
    logger.info("--- [LANGRAPH] 2. PLANNER ---")

    priority_hints = []
    if state["npc_fame"] >= 0.7:
        priority_hints.append("FAME CRITICA (>= 0.7): soddisfare la fame e' la priorita' assoluta. Usa oggetti con effetto 'riduce la fame'.")
    elif state["npc_fame"] >= 0.4:
        priority_hints.append("Fame moderata: considera di mangiare se c'e' cibo disponibile.")
    if state["npc_energy"] >= 0.7:
        priority_hints.append("STANCHEZZA CRITICA (>= 0.7): riposarsi e' la priorita' assoluta. Usa oggetti con effetto 'riduce la stanchezza'.")
    elif state["npc_energy"] >= 0.4:
        priority_hints.append("Stanchezza moderata: considera di riposarti se c'e' un posto adatto.")

    priority_text = "\n".join(priority_hints) if priority_hints else "Nessuna necessita' urgente. Pianifica attivita' libere (esplorare, socializzare, osservare)."

    memories_text = ""
    if state["retrieved_memories"]:
        memories_text = "\nRicordi piu' rilevanti per la situazione attuale:\n"
        for mem in state["retrieved_memories"]:
            age_min = int((time.time() - mem["timestamp"]) / 60)
            memories_text += f"- [{age_min} minuti fa] \"{mem['content']}\" (importanza: {mem['importance']}/10)\n"
    else:
        memories_text = "\nNessun ricordo rilevante. E' la prima volta che agisci.\n"

    reflections_text = ""
    if state["npc_reflections"]:
        reflections_text = "\nRiflessioni sul tuo comportamento passato (usale per migliorare le tue decisioni):\n"
        for r in state["npc_reflections"][-5:]:
            reflections_text += f"- {r}\n"

    system_prompt = (
        "Sei il pianificatore strategico di un NPC in un videogioco 3D.\n"
        "Genera un piano d'azione GERARCHICO per il personaggio.\n\n"
        "STRUTTURA RICHIESTA:\n"
        "1. GOAL: un obiettivo di alto livello in una frase (es. 'Mantenermi in salute', 'Esplorare la zona')\n"
        "2. SOTTO-OBIETTIVI: 1-3 passi per raggiungere il goal, ciascuno con priorita' (ALTA, MEDIA, BASSA)\n"
        "3. AZIONI CONCRETE: per ogni sotto-obiettivo, la sequenza di azioni\n\n"
        "REGOLE:\n"
        "- Usa SOLO oggetti nella sezione 'Oggetti con cui puoi INTERAGIRE'\n"
        "- Ogni interazione richiede: prima raggiungi l'oggetto, poi interagisci\n"
        "- Se fame >= 0.7, soddisfare la fame e' PRIORITA' ASSOLUTA\n"
        "- Se stanchezza >= 0.7, riposarsi e' PRIORITA' ASSOLUTA\n"
        "- NON ripetere azioni gia' presenti nelle azioni recenti\n"
        "- Usa i ricordi recuperati per non ripetere errori passati\n"
        "- Se le riflessioni suggeriscono di cambiare abitudini, seguile\n"
        "- Se non ci sono bisogni urgenti, pianifica attivita' esplorative o sociali\n"
        "- Massimo 3 sotto-obiettivi, massimo 4 azioni totali\n\n"
        "FORMATO RISPOSTA:\n"
        "GOAL: <obiettivo>\n"
        "SOTTO-OBIETTIVO 1 [PRIORITA: ALTA]: <descrizione>\n"
        "  - Azione: Vai a <oggetto>\n"
        "  - Azione: <azione> su <oggetto>\n"
        "SOTTO-OBIETTIVO 2 [PRIORITA: MEDIA]: <descrizione>\n"
        "  - Azione: Vai a <oggetto>\n"
        "  - Azione: <azione> su <oggetto>\n"
    )

    prompt = (
        f"{state['scene_summary']}\n\n"
        f"Indicazioni di priorita':\n{priority_text}\n"
        f"{memories_text}\n"
        f"{reflections_text}\n"
        "Genera il piano gerarchico:"
    )

    try:
        piano = ollama.generate(system_prompt, prompt, temperature=0.3)
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Errore Planner: {e}")

    logger.info(f"[PLANNER] Piano:\n{piano}")
    return {"current_plan": piano}


def builder_node(state: AgentState) -> Dict[str, Any]:
    logger.info(f"--- [LANGRAPH] 3. BUILDER (tentativo {state['retry_count'] + 1}/{MAX_RETRIES}) ---")

    oggetti_info = []
    for name, action in state["valid_actions_map"].items():
        if action.lower() == "nessuna":
            continue
        dist = state["valid_distances"].get(name, 999.0)
        vicino = dist < 1.0
        dist_str = f"VICINO ({dist:.1f}m): NON serve MoveToTargetAndWait" if vicino else f"Distanza: {dist:.1f}m"
        oggetti_info.append(f"  - \"{name}\" -> azione: \"{action}\" | {dist_str}")

    lista_oggetti = "\n".join(oggetti_info) if oggetti_info else "  (nessun oggetto interagibile nella scena)"

    retry_warning = ""
    if state["retry_count"] >= 1:
        retry_warning = f"\nTENTATIVO {state['retry_count'] + 1} di {MAX_RETRIES}. Correggi l'errore precedente.\n"
    if state["retry_count"] >= 2:
        retry_warning += "ULTIMO TENTATIVO. NON usare placeholder o commenti al posto del codice.\n"

    error_section = ""
    if state["has_compile_error"] and state["compile_error"]:
        error_section = (
            f"\n\nERRORE DEL TENTATIVO PRECEDENTE:\n{state['compile_error']}\n"
            "CORREGGI RIGOROSAMENTE questo errore.\n"
        )

    system_prompt = f"""Sei il Builder C# per Unity. Traduci il piano in uno script 'AiAction'.

STRUTTURA OBBLIGATORIA:
using UnityEngine;
using System.Collections;

public class AiAction : MonoBehaviour
{{
    void Start()
    {{
        StartCoroutine(ExecutePlan());
    }}

    IEnumerator ExecutePlan()
    {{
        NPCController controller = GetComponent<NPCController>();
        if (controller == null) yield break;

        // SOTTO-OBIETTIVO: <nome> [PRIORITA: <livello>]
        yield return StartCoroutine(controller.MoveToTargetAndWait("NOME_OGGETTO"));
        yield return StartCoroutine(controller.InteractWithAndWait("NOME_OGGETTO", "NOME_AZIONE"));

        Destroy(this);
        yield break;
    }}
}}

FUNZIONI DISPONIBILI (nient'altro):
1. yield return StartCoroutine(controller.MoveToTargetAndWait("NOME_OGGETTO"));
2. yield return StartCoroutine(controller.InteractWithAndWait("NOME_OGGETTO", "NOME_AZIONE"));

REGOLE FONDAMENTALI:
- Se un oggetto e' segnato "VICINO: NON serve MoveToTargetAndWait", NON generare MoveToTargetAndWait per quell'oggetto. Vai diretto a InteractWithAndWait.
- NON chiamare ModifyStat (non esiste).
- NON usare GameObject.Find, SetActive o inventare nomi di controller.
- Il controller si chiama SEMPRE NPCController.
- Gli effetti fisiologici (fame, stanchezza) sono applicati AUTOMATICAMENTE da Unity dopo ogni InteractWithAndWait. NON chiamare ModifyStat, GetFame, GetEnergy o qualsiasi altra funzione che non sia MoveToTargetAndWait o InteractWithAndWait.
- NON usare placeholder, TODO o commenti al posto di codice.
- Ogni interazione deve essere: MoveToTargetAndWait (se non vicino) + InteractWithAndWait.
- NON dimenticare il secondo argomento di InteractWithAndWait (il nome azione).

OGGETTI E AZIONI VALIDI IN QUESTA SCENA:
{lista_oggetti}

USA SOLO questi nomi e azioni. Qualsiasi altro nome e' VIETATO.
In fondo a ExecutePlan() metti sempre: Destroy(this); yield break;{retry_warning}{error_section}"""

    prompt = (
        f"Scena:\n{state['scene_summary']}\n\n"
        f"Piano da tradurre in codice:\n{state['current_plan']}\n\n"
        "Genera SOLO il codice C# completo per 'AiAction'. Nessun commento fuori dal codice."
    )

    try:
        code = ollama.generate(system_prompt, prompt, temperature=0.0)
        code = strip_markdown_fences(code)
        code = fix_controller_name(code)
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Errore Builder: {e}")

    logger.info("[BUILDER] Codice C# generato.")
    return {
        "generated_code": code,
        "retry_count": state["retry_count"] + 1,
    }


def inspector_node(state: AgentState) -> Dict[str, Any]:
    logger.info("--- [LANGRAPH] 4. INSPECTOR ---")

    if state["has_compile_error"]:
        logger.warning(f"[INSPECTOR] Errore preesistente da Unity: {state['compile_error']}")
        return {"has_compile_error": True}

    placeholder_issues = find_placeholder_issues(state["generated_code"])
    if placeholder_issues:
        logger.warning(f"[INSPECTOR] Placeholder trovati: {placeholder_issues}")
        return {
            "has_compile_error": True,
            "compile_error": "Il codice contiene pseudo-codice o placeholder: " + " ".join(placeholder_issues),
        }

    invalid = validate_generated_code(state["generated_code"], state["valid_object_names"])
    if invalid:
        logger.warning(f"[INSPECTOR] Oggetti non validi: {invalid}")
        return {
            "has_compile_error": True,
            "compile_error": f"Il codice usa oggetti non presenti nella scena: {invalid}. Usa SOLO i nomi della lista valida.",
        }

    logger.info("[INSPECTOR] Codice valido.")
    return {"has_compile_error": False, "compile_error": ""}


def decide_next_step(state: AgentState) -> str:
    if state["has_compile_error"] and state["retry_count"] < MAX_RETRIES:
        logger.info(f"[ROUTER] Retry Builder ({state['retry_count']}/{MAX_RETRIES})")
        return "builder"
    if state["has_compile_error"]:
        logger.error("[ROUTER] Tentativi esauriti.")
    return END


# ==============================================================================
# GRAFO LANGGRAPH
# ==============================================================================

workflow = StateGraph(AgentState)
workflow.add_node("retrieval", retrieval_node)
workflow.add_node("planner", planner_node)
workflow.add_node("builder", builder_node)
workflow.add_node("inspector", inspector_node)

workflow.add_edge(START, "retrieval")
workflow.add_edge("retrieval", "planner")
workflow.add_edge("planner", "builder")
workflow.add_edge("builder", "inspector")
workflow.add_conditional_edges("inspector", decide_next_step, {"builder": "builder", END: END})

compiled_graph = workflow.compile()


# ==============================================================================
# REFLECTOR (attivato dopo ogni azione, esegue ogni REFLECTION_INTERVAL)
# ==============================================================================

def maybe_reflect():
    memories = load_memory_stream()
    action_memories = [m for m in memories if m["type"] in ("action", "observation")]
    if len(action_memories) == 0:
        return

    if len(action_memories) % REFLECTION_INTERVAL != 0:
        return

    logger.info(f"[REFLECTOR] Attivazione dopo {len(action_memories)} azioni.")

    recent = action_memories[-20:]
    recent_text = "\n".join(f"- {m['content']}" for m in recent)

    system_prompt = (
        "Sei il modulo di Riflessione di un NPC in un videogioco.\n"
        "Analizza le esperienze recenti e genera da 1 a 3 riflessioni di alto livello "
        "sul comportamento dell'NPC. Identifica pattern, abitudini, problemi ricorrenti "
        "o opportunita' di miglioramento.\n\n"
        "REGOLE:\n"
        "1. Scrivi in italiano, in terza persona (es. 'L\\'NPC tende a...')\n"
        "2. Ogni riflessione: massimo 25 parole\n"
        "3. Una riflessione per riga\n"
        "4. Basati SOLO sulle esperienze fornite\n"
        "5. NON descrivere singole azioni, ma pattern generali"
    )

    prompt = (
        f"Esperienze recenti dell'NPC:\n{recent_text}\n\n"
        "Genera le riflessioni (una per riga):"
    )

    try:
        result = ollama.generate(system_prompt, prompt, temperature=0.5)
        reflections = [r.strip("- ").strip() for r in result.split("\n") if r.strip()]
        reflections = [r for r in reflections if len(r) > 5][:3]
    except Exception as e:
        logger.warning(f"[REFLECTOR] Errore: {e}")
        return

    for ref in reflections:
        embedding = ollama.embed(ref)
        add_memory(ref, "reflection", 9, embedding)
        logger.info(f"[REFLECTOR] Nuova riflessione: {ref}")


# ==============================================================================
# ENDPOINT FASTAPI
# ==============================================================================

@app.post("/npc/decide")
async def ricevi_scena_e_decidi(report: ReportScena):
    scene_summary = build_scene_summary(report)
    valid_object_names = [obj.name for obj in report.oggetti_vicini]
    valid_actions_map = {obj.name: obj.available_action for obj in report.oggetti_vicini}
    valid_distances = {obj.name: obj.distance for obj in report.oggetti_vicini}
    valid_effects = {
        obj.name: infer_effect(obj.available_action, obj.name, obj.tag, obj.effect)
        for obj in report.oggetti_vicini
    }
    has_error = bool(report.compile_error)

    stato_iniziale: AgentState = {
        "npc_goal": report.npc_goal,
        "npc_fame": report.npc_fame,
        "npc_energy": report.npc_energy,
        "last_actions": report.last_actions or [],
        "npc_reflections": report.npc_reflections or [],
        "scene_summary": scene_summary,
        "retrieved_memories": [],
        "valid_object_names": valid_object_names,
        "valid_actions_map": valid_actions_map,
        "valid_distances": valid_distances,
        "valid_effects": valid_effects,
        "current_plan": "",
        "generated_code": "",
        "has_compile_error": has_error,
        "compile_error": report.compile_error or "",
        "retry_count": 0,
    }

    stato_finale = compiled_graph.invoke(stato_iniziale)

    if stato_finale.get("has_compile_error"):
        raise HTTPException(
            status_code=422,
            detail={
                "error": "Impossibile generare codice valido dopo i tentativi massimi.",
                "last_error": stato_finale.get("compile_error"),
                "last_code": stato_finale.get("generated_code"),
            },
        )

    return {
        "status": "success",
        "plan": stato_finale["current_plan"],
        "code": stato_finale["generated_code"],
        "npc_reflections": stato_finale["npc_reflections"],
    }


@app.post("/npc/action-done")
async def azione_completata(report: ActionDoneReport):
    importance = score_importance(report.action_description)
    embedding = ollama.embed(report.action_description)
    add_memory(report.action_description, "action", importance, embedding)
    maybe_reflect()
    return {"status": "ok"}


@app.get("/health")
async def health():
    return {
        "status": "ok",
        "model": OLLAMA_MODEL,
        "embed_model": EMBED_MODEL,
        "memories_count": len(load_memory_stream()),
    }


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=8000)