using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.Networking;

public class SceneAnalyzer : MonoBehaviour
{
    [Header("Riferimenti")]
    public RuntimeCompiler compiler;

    [Header("Parametri di Percezione")]
    public float perceptionRadius = 15f;

    [Header("Configurazione Server")]
    public string serverUrl = "http://127.0.0.1:8000/npc/decide";

    [System.Serializable]
    public class ContextObject
    {
        public string name;
        public string tag;
        public float distance;
        public string available_action; // <--- AGGIUNTO: Ora Python saprà l'azione!
    }

    [System.Serializable]
    public class SceneReport
    {
        public string npc_goal;
        public float npc_fame;
        public List<ContextObject> oggetti_vicini;
        public string compile_error;
    }

    [System.Serializable]
    public class PythonServerResponse
    {
        public string status;
        public string scene_summary;
        public string plan;
        public string code;
    }

    public string GatherSceneData(string error = "")
    {
        SceneReport report = new SceneReport();
        report.compile_error = error;

        NPCController controller = GetComponent<NPCController>();
        if (controller != null)
        {
            report.npc_fame = controller.fame;
            if (controller.fame > 1f)
            {
                report.npc_goal = "Trova qualcosa da mangiare perché ho molta fame";
            }
            else
            {
                report.npc_goal = "Comportati in modo realistico a seconda della scena";
            }
        }
        else
        {
            report.npc_fame = 0.5f;
            report.npc_goal = "Esplora la zona";
        }

        report.oggetti_vicini = new List<ContextObject>();

        Collider[] hitColliders = Physics.OverlapSphere(transform.position, perceptionRadius);

        foreach (var collider in hitColliders)
        {
            if (collider.gameObject == this.gameObject) continue;
            if (collider.CompareTag("Untagged")) continue;

            ContextObject obj = new ContextObject();
            obj.name = collider.gameObject.name;
            obj.tag = collider.tag;
            obj.distance = Vector3.Distance(transform.position, collider.transform.position);

            // CORREZIONE 1: Cerchiamo lo script sul collider (l'oggetto 3D vero), non su obj
            InteractableObject interactable = collider.gameObject.GetComponent<InteractableObject>();

            string azioneDisponibile = "Nessuna";

            // Se l'oggetto ha il nostro script attaccato, leggiamo la sua azione!
            if (interactable != null)
            {
                azioneDisponibile = interactable.actionName;
            }

            // CORREZIONE 2: Assegniamo l'azione al nostro oggetto da inviare a Python!
            obj.available_action = azioneDisponibile;

            if (!report.oggetti_vicini.Exists(x => x.name == obj.name))
            {
                report.oggetti_vicini.Add(obj);
            }
        }

        return JsonUtility.ToJson(report, true);
    }

    IEnumerator SendDataToServer(string jsonText)
    {
        using (UnityWebRequest request = new UnityWebRequest(serverUrl, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonText);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            Debug.Log("[UNITY] Invio dati a LangGraph...");
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                PythonServerResponse serverData = JsonUtility.FromJson<PythonServerResponse>(request.downloadHandler.text);

                if (!string.IsNullOrEmpty(serverData.code))
                {
                    Debug.Log("[UNITY -> ROSLYN] Tentativo di compilazione...");

                    Debug.Log("<color=yellow>[CODICE IA GENERATO]:</color>\n" + serverData.code);

                    string risultatoCompilazione = compiler.CompileAndAttachCode(serverData.code, this.gameObject);
                    if (!string.IsNullOrEmpty(risultatoCompilazione))
                    {
                        Debug.LogError($"[UNITY] Rilevato errore Roslyn: {risultatoCompilazione}. Attivo l'auto-correzione...");
                        string jsonConErrore = GatherSceneData(risultatoCompilazione);
                        StartCoroutine(SendDataToServer(jsonConErrore));
                    }
                    else
                    {
                        Debug.Log("[UNITY] Compilazione riuscita! L'NPC sta eseguendo lo script.");
                    }
                }
            }
            else
            {
                Debug.LogError($"[UNITY] Errore di comunicazione: {request.error}");
            }
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            string jsonScena = GatherSceneData();
            StartCoroutine(SendDataToServer(jsonScena));
        }
    }
}