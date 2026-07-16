using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;

public class SceneAnalyzer : MonoBehaviour
{
    [Header("Riferimenti")]
    public RuntimeCompiler compiler;

    [Header("Parametri di Percezione")]
    public float perceptionRadius = 15f;

    [Header("Configurazione Server")]
    public string serverUrl = "http://127.0.0.1:8000";

    [Header("Parametri Retry")]
    public int maxRetries = 3;
    public float retryDelay = 1.0f;

    [HideInInspector] public List<string> lastActions = new List<string>();
    [HideInInspector] public List<string> npcReflections = new List<string>();
    [HideInInspector] public string currentGoal = "Comportati in modo realistico";

    private int _currentRetry = 0;
    private bool _isRequestInProgress = false;
    private NPCController _controller;

    public bool IsRequestInProgress => _isRequestInProgress;

    [System.Serializable]
    public class ContextObject
    {
        public string name;
        public string tag;
        public float distance;
        public string available_action;
        public string effect;
    }

    [System.Serializable]
    public class SceneReport
    {
        public string npc_goal;
        public float npc_fame;
        public float npc_energy;
        public List<ContextObject> oggetti_vicini;
        public List<string> last_actions;
        public List<string> npc_reflections;
        public string compile_error;
    }

    [System.Serializable]
    public class PythonServerResponse
    {
        public string status;
        public string plan;
        public string code;
        public List<string> npc_reflections;
    }

    [System.Serializable]
    public class ActionDoneReport
    {
        public string action_description;
        public float npc_fame;
        public float npc_energy;
    }

    void Awake()
    {
        _controller = GetComponent<NPCController>();
    }

    private string GetEffectFromInteractable(InteractableObject interactable)
    {
        if (interactable == null) return "none";

        bool riduceFame = interactable.fameEffect < 0;
        bool riduceStanchezza = interactable.energyEffect < 0;

        if (riduceFame && riduceStanchezza) return "both";
        if (riduceFame) return "hunger";
        if (riduceStanchezza) return "energy";
        return "none";
    }

    public string GatherSceneData(string error = "")
    {
        SceneReport report = new SceneReport();
        report.compile_error = error;
        report.last_actions = new List<string>(lastActions);
        report.npc_reflections = new List<string>(npcReflections);

        if (_controller != null)
        {
            report.npc_fame = _controller.fame;
            report.npc_energy = _controller.energy;

            if (_controller.fame >= 0.7f)
                report.npc_goal = "Hai molta fame: trova qualcosa da mangiare al piu' presto";
            else if (_controller.energy >= 0.7f)
                report.npc_goal = "Sei molto stanco: cerca un posto dove riposarti";
            else
                report.npc_goal = currentGoal;
        }
        else
        {
            report.npc_fame = 0.5f;
            report.npc_energy = 0.5f;
            report.npc_goal = "Esplora la zona";
        }

        report.oggetti_vicini = new List<ContextObject>();

        Collider[] hitColliders = Physics.OverlapSphere(transform.position, perceptionRadius);
        foreach (var col in hitColliders)
        {
            if (col.gameObject == gameObject) continue;
            if (col.CompareTag("Untagged")) continue;

            string objName = col.gameObject.name;
            if (report.oggetti_vicini.Exists(x => x.name == objName)) continue;

            InteractableObject interactable = col.gameObject.GetComponent<InteractableObject>();

            ContextObject obj = new ContextObject
            {
                name = objName,
                tag = col.tag,
                distance = Vector3.Distance(transform.position, col.transform.position),
                available_action = interactable != null ? interactable.actionName : "Nessuna",
                effect = GetEffectFromInteractable(interactable)
            };

            report.oggetti_vicini.Add(obj);
        }

        return JsonUtility.ToJson(report, true);
    }

    public void TriggerPlanning()
    {
        if (_isRequestInProgress)
        {
            Debug.LogWarning("[SceneAnalyzer] Richiesta gia' in corso, ignoro.");
            return;
        }
        _currentRetry = 0;
        _isRequestInProgress = true;
        string json = GatherSceneData();
        StartCoroutine(SendDataToServer(json));
    }

    private void FinishPlanningCycle()
    {
        _isRequestInProgress = false;
    }

    IEnumerator SendDataToServer(string jsonText)
    {
        if (_currentRetry >= maxRetries)
        {
            Debug.LogError($"[SceneAnalyzer] Raggiunto il limite di {maxRetries} tentativi totali.");
            FinishPlanningCycle();
            yield break;
        }

        Debug.Log($"[SceneAnalyzer] Invio dati al server (tentativo {_currentRetry + 1}/{maxRetries})...");

        using (UnityWebRequest request = new UnityWebRequest(serverUrl + "/npc/decide", "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonText);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.responseCode == 422)
            {
                Debug.LogError("[SceneAnalyzer] Server ha esaurito i tentativi di correzione (422).");
                _currentRetry++;
                if (_currentRetry < maxRetries)
                {
                    Debug.LogWarning($"[SceneAnalyzer] Nuovo tentativo pulito ({_currentRetry + 1}/{maxRetries})...");
                    string freshJson = GatherSceneData();
                    StartCoroutine(SendDataToServer(freshJson));
                }
                else
                {
                    FinishPlanningCycle();
                }
                yield break;
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[SceneAnalyzer] Errore HTTP: {request.error}");
                _currentRetry++;
                if (_currentRetry < maxRetries)
                {
                    Debug.LogWarning($"[SceneAnalyzer] Ritento dopo errore HTTP ({_currentRetry + 1}/{maxRetries})...");
                    yield return new WaitForSeconds(retryDelay);
                    StartCoroutine(SendDataToServer(jsonText));
                }
                else
                {
                    FinishPlanningCycle();
                }
                yield break;
            }

            PythonServerResponse serverData =
                JsonUtility.FromJson<PythonServerResponse>(request.downloadHandler.text);

            if (string.IsNullOrEmpty(serverData.code))
            {
                Debug.LogError("[SceneAnalyzer] Server ha risposto senza codice.");
                FinishPlanningCycle();
                yield break;
            }

            if (serverData.npc_reflections != null && serverData.npc_reflections.Count > 0)
            {
                npcReflections = serverData.npc_reflections;
                Debug.Log($"[SceneAnalyzer] Reflections aggiornate ({npcReflections.Count} totali).");
            }

            if (!string.IsNullOrEmpty(serverData.plan))
            {
                string goalLine = serverData.plan.Split('\n')[0];
                if (goalLine.StartsWith("GOAL:"))
                {
                    currentGoal = goalLine.Substring(5).Trim();
                    Debug.Log($"[SceneAnalyzer] Goal aggiornato: {currentGoal}");
                }
            }

            Debug.Log("[CODICE IA GENERATO]:\n" + serverData.code);
            Debug.Log("[SceneAnalyzer] Compilazione in corso...");

            string erroreCompilazione = compiler.CompileAndAttachCode(serverData.code, gameObject);

            if (!string.IsNullOrEmpty(erroreCompilazione))
            {
                _currentRetry++;
                if (_currentRetry >= maxRetries)
                {
                    Debug.LogError($"[SceneAnalyzer] Raggiunto limite di {maxRetries} tentativi.");
                    FinishPlanningCycle();
                    yield break;
                }

                Debug.LogWarning($"[SceneAnalyzer] Errore compilazione (tentativo {_currentRetry}/{maxRetries}): {erroreCompilazione}");
                string jsonConErrore = GatherSceneData(erroreCompilazione);
                StartCoroutine(SendDataToServer(jsonConErrore));
            }
            else
            {
                Debug.Log("[SceneAnalyzer] Compilazione riuscita. L'NPC esegue lo script.");
                FinishPlanningCycle();
            }
        }
    }

    public void NotifyActionDone(string actionDescription)
    {
        StartCoroutine(SendActionDone(actionDescription));
    }

    IEnumerator SendActionDone(string actionDescription)
    {
        ActionDoneReport report = new ActionDoneReport
        {
            action_description = actionDescription,
            npc_fame = _controller != null ? _controller.fame : 0.5f,
            npc_energy = _controller != null ? _controller.energy : 0.5f
        };

        string json = JsonUtility.ToJson(report);

        using (UnityWebRequest request = new UnityWebRequest(serverUrl + "/npc/action-done", "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[SceneAnalyzer] Impossibile notificare azione al server: {request.error}");
            }
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            TriggerPlanning();
        }
    }
}