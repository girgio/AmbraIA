using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class NPCClient : MonoBehaviour
{
    [Header("Server")]
    public string serverUrl = "http://localhost:5000/npc/decide";

    [Header("Vista")]
    public float raggioVista = 10f;
    [Range(0, 360)] public float angoloVista = 90f;

    [Header("Materiali")]
    public Material foodMaterial;

    // ── Stato interno ────────────────────────────────────────────────────────
    private GameObject currentTarget = null;
    private bool isProcessing = false;
    private bool isActing = false;
    private float timer = 0f;
    private const float intervallo = 2.0f;

    // Movimento
    private float target_x;
    private float target_z;
    private Vector3 posizioneIniziale;

    // Fame: cresce da 0 a 1 in 2 minuti (120 secondi)
    private float fame = 0f;
    private const float fameCrescita = 1f / 60f;

    // Dati da inviare al server
    private NpcData data = new NpcData();

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    void Start()
    {
        posizioneIniziale = transform.position;
        target_x = posizioneIniziale.x;
        target_z = posizioneIniziale.z;
    }

    void Update()
    {
        // Fame cresce col tempo
        fame = Mathf.Clamp01(fame + fameCrescita * Time.deltaTime);

        // Movimento verso il target
        float moveSpeed = 2f * Time.deltaTime;
        float lambda = 0.6f;
        float hor = 0f;
        float ver = 0f;

        if (!isProcessing)
        {
            if (Mathf.Abs(target_x - transform.position.x) > lambda)
                hor = target_x > transform.position.x ? 1f : -1f;

            if (Mathf.Abs(target_z - transform.position.z) > lambda)
                ver = target_z > transform.position.z ? 1f : -1f;
        }

        Vector3 movement = new Vector3(hor, 0, ver).normalized * moveSpeed;
        transform.Translate(movement, Space.World);

        // Rotazione in base alla direzione
        if (ver > 0) transform.rotation = Quaternion.Euler(0, 0, 0);
        else if (ver < 0) transform.rotation = Quaternion.Euler(0, 180, 0);
        else if (hor > 0) transform.rotation = Quaternion.Euler(0, 90, 0);
        else if (hor < 0) transform.rotation = Quaternion.Euler(0, -90, 0);

        // Timer FOV e movimento random
        timer += Time.deltaTime;

        if (timer >= intervallo)
        {
            ControllaFOV();
            isActing = false;
            RandomMove();
            timer = 0f;
        }
        else if (!isActing)
        {
            ControllaFOV();
        }
    }

    // ── FOV ──────────────────────────────────────────────────────────────────

    void ControllaFOV()
    {
        Collider[] targetNelRaggio = Physics.OverlapSphere(transform.position, raggioVista);

        foreach (var target in targetNelRaggio)
        {
            if (target.CompareTag("Untagged") || isProcessing) continue;

            Vector3 direzioneTarget = (target.transform.position - transform.position).normalized;

            if (Vector3.Angle(transform.forward, direzioneTarget) >= angoloVista / 2) continue;

            float distanzaTarget = Vector3.Distance(transform.position, target.transform.position);

            // Fix raycast: controlla cosa ha colpito
            if (Physics.Raycast(transform.position + Vector3.up, direzioneTarget,
                                 out RaycastHit hit, distanzaTarget))
            {
                // Il raggio ha colpito qualcosa: procedi solo se è l'oggetto stesso
                if (hit.collider.gameObject != target.gameObject) continue;
            }
            // Se non ha colpito nulla → strada libera, procedi comunque

            isProcessing = true;
            currentTarget = target.gameObject;
            Debug.Log($"Vedo: {target.tag} '{target.name}' a {distanzaTarget:F1}m | Fame: {fame:F2}");
            ChiediAzione($"Vedo un oggetto di tipo {target.tag} nome: {target.name}", distanzaTarget);
        }
    }

    // ── Comunicazione server ─────────────────────────────────────────────────

    public void ChiediAzione(string descrizioneAmbiente, float distanza)
    {
        StartCoroutine(PostRequest(descrizioneAmbiente, distanza));
    }

    IEnumerator PostRequest(string descrizione, float distanza)
    {
        // Leggi valore nutritivo dall'oggetto (default 0.3 se non ha FoodData)
        FoodData foodData = currentTarget != null ? currentTarget.GetComponent<FoodData>() : null;
        float valoreNutritivo = foodData != null ? foodData.valoreNutritivo : 0.0f;

        data.descrizione = descrizione;
        data.distanza = distanza;
        data.nomeOggetto = currentTarget != null ? currentTarget.name : "";
        data.tipoOggetto = currentTarget != null ? currentTarget.tag : "";
        data.fame = fame;
        data.valoreNutritivo = valoreNutritivo;

        string json = JsonUtility.ToJson(data);
        var request = new UnityWebRequest(serverUrl, "POST");
        request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            Debug.Log("Risposta NPC: " + request.downloadHandler.text);
            NpcAnswer dati = JsonUtility.FromJson<NpcAnswer>(request.downloadHandler.text);

            // Sincronizza la fame col server
            fame = dati.nuovaFame;
            Debug.Log($"Azione: {dati.azione} | Nuova fame: {fame:F2}");

            EseguiAzione(dati.azione);
        }
        else
        {
            Debug.LogError("Errore richiesta: " + request.error);
        }

        isProcessing = false;
    }

    // ── Esecuzione azioni ────────────────────────────────────────────────────

    void EseguiAzione(string azione)
    {
        if (currentTarget == null && azione != "do_nothing" && azione != "sit_down")
        {
            Debug.LogWarning("EseguiAzione: currentTarget è null, skip.");
            return;
        }

        float distanza = currentTarget != null
            ? Vector3.Distance(transform.position, currentTarget.transform.position)
            : 0f;

        FoodData foodData = currentTarget != null ? currentTarget.GetComponent<FoodData>() : null;
        float valoreNutritivo = foodData != null ? foodData.valoreNutritivo : 0.0f;
        float reward = 0f;

        switch (azione)
        {
            // ── Mangia ───────────────────────────────────────────────────────
            case "eat_food":
                Debug.Log("GNAM! L'NPC ha mangiato.");

                if (currentTarget.CompareTag("Cibo"))
                {
                    // Reward proporzionale a fame e valore nutritivo
                    reward = fame * valoreNutritivo;

                    if (currentTarget.name.Contains("cucinato"))
                        reward += 0.1f; // bonus cottura
                }
                else if (currentTarget.CompareTag("Ingrediente"))
                {
                    reward = currentTarget.name.Contains("cucinato") ? -0.6f : -0.7f;
                    reward += fame * valoreNutritivo;
                }
                else
                {
                    reward = -1f;
                }

                if (distanza >= 2f) reward -= 1f; // penalità distanza

                reward = Mathf.Clamp(reward, -1f, 1f);

                data.ultimoReward = reward;
                data.ultimoRisultato = $"Hai mangiato: {currentTarget.name}";

                currentTarget.SetActive(false);
                currentTarget = null;
                break;

            // ── Cucina ───────────────────────────────────────────────────────
            case "cook":
                // Salva il tag PRIMA di modificarlo per la logica reward
                bool eraIngrediente = currentTarget.CompareTag("Ingrediente");
                bool eraCibo = currentTarget.CompareTag("Cibo");
                bool eraCucinato = currentTarget.name.Contains("cucinato");
                float valoreAggiunto = 0.2f;

                // Calcola reward sul tag originale
                if (eraIngrediente)
                {
                    reward = eraCucinato ? 0.6f : 1f;

                    if(foodData != null)
                    {
                        foodData.valoreNutritivo += valoreAggiunto;

                        if (foodData.valoreNutritivo >= 1)
                        {
                            foodData.valoreNutritivo = 1;
                        }
                    }
                }
                else if (eraCibo)
                {
                    reward = eraCucinato ? -0.9f : -0.8f;
                    
                    if (foodData != null)
                    {
                        foodData.valoreNutritivo -= valoreAggiunto;//cucinare un cibo già pronto fa perdere valore nutritivo

                        if(foodData.valoreNutritivo <= 0)
                        {
                            foodData.valoreNutritivo = 0;
                        }
                    }
                }
                else
                {
                    reward = -1f;
                }

                if (distanza < 2f) reward += 0.5f;
                else reward -= 1f;

                reward = Mathf.Clamp(reward, -1f, 1f);

                // Ora modifica l'oggetto
                currentTarget.tag = "Cibo";
                currentTarget.GetComponent<Renderer>().material = foodMaterial;
                currentTarget.name = currentTarget.name + " cucinato";

                data.ultimoReward = reward;
                data.ultimoRisultato = $"Hai cucinato: {currentTarget.name}";
                currentTarget = null;
                break;

            // ── Muoviti ──────────────────────────────────────────────────────
            case "move":
                Debug.Log("L'NPC si muove verso " + currentTarget.name);
                target_x = currentTarget.transform.position.x;
                target_z = currentTarget.transform.position.z;

                reward = data.distanza > 2f ? 1f : -1f;

                data.ultimoReward = reward;
                data.ultimoRisultato = $"Ti sei avvicinato a {currentTarget.name}";
                isActing = true;
                timer = 0f;
                break;

            // ── Siediti ──────────────────────────────────────────────────────
            case "sit_down":
                Debug.Log("L'NPC si è seduto.");
                reward = -1f;
                data.ultimoReward = reward;
                data.ultimoRisultato = "Ti sei seduto.";
                break;

            // ── Non fare nulla ───────────────────────────────────────────────
            case "do_nothing":
                Debug.Log("L'NPC non fa nulla.");
                reward = -0.1f;
                data.ultimoReward = reward;
                data.ultimoRisultato = "Non hai fatto nulla.";
                break;

            default:
                Debug.LogWarning("Azione non riconosciuta: " + azione);
                break;
        }

        Debug.Log($"Reward: {reward:F2}");
    }

    // ── Movimento random ─────────────────────────────────────────────────────

    void RandomMove()
    {
        target_x = Random.Range(posizioneIniziale.x - 10f, posizioneIniziale.x + 10f);
        target_z = Random.Range(posizioneIniziale.z - 10f, posizioneIniziale.z + 10f);
    }

    // ── Gizmos debug ─────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        Gizmos.color = Color.white;
        Gizmos.DrawWireSphere(transform.position, raggioVista);

        Vector3 vistaSinistra = Quaternion.Euler(0, -angoloVista / 2, 0) * transform.forward;
        Vector3 vistaDestra = Quaternion.Euler(0, angoloVista / 2, 0) * transform.forward;

        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(transform.position, vistaSinistra * raggioVista);
        Gizmos.DrawRay(transform.position, vistaDestra * raggioVista);

        // Mostra barra fame nell'editor (rosso = affamato)
        Gizmos.color = Color.Lerp(Color.green, Color.red, fame);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 2f, 0.2f);
    }
}
