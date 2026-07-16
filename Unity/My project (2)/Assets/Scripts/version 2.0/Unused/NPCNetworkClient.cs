using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class NpcNetworkClient : MonoBehaviour
{
    private SceneAnalyzer sceneAnalyzer;

    [Header("Configurazione Server")]
    public string serverUrl = "http://127.0.0.1:8000/npc/decide";

    void Start()
    {
        // Recupera lo SceneAnalyzer attaccato allo stesso NPC
        sceneAnalyzer = GetComponent<SceneAnalyzer>();
    }

    void Update()
    {
        // Cambiamo il tasto di test: premi "G" (come Go!) per mandare i dati a Python
        if (Input.GetKeyDown(KeyCode.G))
        {
            StartCoroutine(PostSceneData());
        }
    }

    IEnumerator PostSceneData()
    {
        // 1. Prendi il JSON generato dallo SceneAnalyzer
        string jsonPayload = sceneAnalyzer.GatherSceneData();
        Debug.Log("[CLIENT] Invio del report della scena al server Python...");

        // 2. Configura la richiesta HTTP POST
        UnityWebRequest request = new UnityWebRequest(serverUrl, "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonPayload);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();

        // Specifichiamo al server che stiamo mandando un JSON
        request.SetRequestHeader("Content-Type", "application/json");

        // 3. Spedisci la richiesta e aspetta la risposta (senza bloccare il gioco)
        yield return request.SendWebRequest();

        // 4. Gestione della risposta
        if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.ProtocolError)
        {
            Debug.LogError("[CLIENT] Errore di comunicazione con Python: " + request.error);
        }
        else
        {
            Debug.Log("[CLIENT] Successo! Risposta ricevuta dal server Python:");
            // Stampa nella console di Unity la decisione temporanea che ha preso Python
            Debug.Log(request.downloadHandler.text);
        }
    }
}
