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
    }

    [System.Serializable]
    public class SceneReport
    {
        public string npc_goal;
        public float npc_fame;
        public List<ContextObject> oggetti_vicini;
        public string compile_error; // <--- AGGIUNTO: Permette di mandare l'errore a Python
    }

    [System.Serializable]
    public class PythonServerResponse
    {
        public string status;
        public string scene_summary;
        public string plan;
        public string code;
    }

    // Abbiamo aggiunto il parametro opzionale "error"
    public string GatherSceneData(string error = "")
    {
        SceneReport report = new SceneReport();
        report.compile_error = error; // <--- Se c'è un errore, lo inseriamo nel report!

        NPCController controller = GetComponent<NPCController>();
        if (controller != null)
        {
            report.npc_fame = controller.fame;
            if (controller.fame > 0.6f)
            {
                report.npc_goal = "Trova qualcosa da mangiare perché ho molta fame";
            }
            else
            {
                report.npc_goal = "Esplora l'ambiente circostante e pattuglia la zona";
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

                    Debug.Log("[UNITY -> ROSLYN] Tentativo di compilazione...");
                    string risultatoCompilazione = compiler.CompileAndAttachCode(serverData.code, this.gameObject);
                    if (!string.IsNullOrEmpty(risultatoCompilazione))
                    {
                        // SE LA COMPILAZIONE È FALLITA:
                        Debug.LogError($"[UNITY] Rilevato errore Roslyn: {risultatoCompilazione}. Attivo l'auto-correzione...");

                        // 1. Rigeneriamo il JSON includendo l'errore appena avvenuto
                        string jsonConErrore = GatherSceneData(risultatoCompilazione);

                        // 2. Chiamata automatica ricorsiva! Spediamo l'errore a Python immediatamente
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
            string jsonScena = GatherSceneData(); // Prima chiamata (senza errori)
            StartCoroutine(SendDataToServer(jsonScena));
        }
    }
}