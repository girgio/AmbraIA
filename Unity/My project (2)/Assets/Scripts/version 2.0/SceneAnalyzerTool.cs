using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using UnityEngine.Networking;
using System.Collections;
using UnityEditor.AI;
using Newtonsoft.Json;

#if UNITY_EDITOR
public class SceneAnalyzerTool : EditorWindow
{
    private InteractableTemplate[] templates;
    private Vector2 scrollPos;

    [MenuItem("NPC/Analizza Scena")]
    public static void ShowWindow()
    {
        GetWindow<SceneAnalyzerTool>("Analizza Scena");
    }

    void OnGUI()
    {
        GUILayout.Label("Template Interagibili", EditorStyles.boldLabel);

        if (templates == null || templates.Length == 0)
        {
            templates = Resources.FindObjectsOfTypeAll<InteractableTemplate>();
        }

        if (GUILayout.Button("Ricarica Template"))
        {
            templates = Resources.FindObjectsOfTypeAll<InteractableTemplate>();
        }

        GUILayout.Space(10);
        GUILayout.Label($"Template trovati: {(templates != null ? templates.Length : 0)}");

        if (templates != null)
        {
            scrollPos = GUILayout.BeginScrollView(scrollPos);
            foreach (var t in templates)
            {
                GUILayout.Label($"- {t.nomeTemplate} (azione: {t.actionName}, fame: [{t.fameEffectMin}..{t.fameEffectMax}], energia: [{t.energyEffectMin}..{t.energyEffectMax}])");
            }
            GUILayout.EndScrollView();
        }

        GUILayout.Space(20);

        if (GUILayout.Button("Analizza Scena e Applica InteractableObject", GUILayout.Height(30)))
        {
            if (templates == null || templates.Length == 0)
            {
                Debug.LogError("Nessun template trovato.");
                return;
            }
            AnalizzaScena();
        }

        GUILayout.Space(10);

 if (GUILayout.Button("Prepara Scena Completa (Analizza + NPC + NavMesh)", GUILayout.Height(40)))
{
    if (templates == null || templates.Length == 0)
    {
        Debug.LogError("Nessun template trovato.");
        return;
    }
    PreparaScenaCompleta();
}

GUILayout.Space(10);

if (GUILayout.Button("Pulisci Scena (rimuove tutti gli InteractableObject)", GUILayout.Height(30)))
{
    PulisciScena();
}


    GUILayout.Space(10);

 }
    void PulisciScena()
    {
        InteractableObject[] tutti = FindObjectsOfType<InteractableObject>();
        int count = 0;
        foreach (var io in tutti)
        {
            // Distrugge l'InteractionPoint figlio
            if (io.interactionPoint != null && io.interactionPoint.parent == io.transform)
            {
                DestroyImmediate(io.interactionPoint.gameObject);
            }
            // Rimuove il componente
            DestroyImmediate(io);
            count++;
        }
        Debug.Log($"[PULISCI SCENA] Rimossi {count} InteractableObject.");
    }

    void PreparaScenaCompleta()
    {
        Debug.Log("[PREPARA SCENA] Inizio preparazione completa...");
        AnalizzaScena();
        AggiungiNPC();
        PreparaOggettiPerNavMesh();
        BakeNavMesh();
        Debug.Log("[PREPARA SCENA] Preparazione completata.");
    }

    void AnalizzaScena()
    {
        GameObject[] tuttiOggetti = FindObjectsOfType<GameObject>();
        List<GameObject> daAnalizzare = new List<GameObject>();
        Dictionary<GameObject, InteractableTemplate> assegnazioni = new Dictionary<GameObject, InteractableTemplate>();

        foreach (GameObject obj in tuttiOggetti)
        {
            if (obj.CompareTag("Untagged")) continue;
            if (obj.GetComponent<InteractableObject>() != null) continue;

            bool matchato = false;
            foreach (InteractableTemplate template in templates)
            {
                if (template.Matcha(obj))
                {
                    assegnazioni[obj] = template;
                    matchato = true;
                    break;
                }
            }
            if (!matchato)
            {
                daAnalizzare.Add(obj);
            }
        }

        Debug.Log($"[ANALIZZA SCENA] Match automatico: {assegnazioni.Count} oggetti");
        Debug.Log($"[ANALIZZA SCENA] Da inviare all'LLM: {daAnalizzare.Count} oggetti");

        foreach (var coppia in assegnazioni)
        {
            ApplicaTemplateConValoriPredefiniti(coppia.Key, coppia.Value);
        }

        if (daAnalizzare.Count > 0)
        {
            InviaAllLLM(daAnalizzare);
        }
        else
        {
            Debug.Log("[ANALIZZA SCENA] Tutti gli oggetti matchati automaticamente.");
        }
    }

    void ApplicaTemplateConValoriPredefiniti(GameObject obj, InteractableTemplate template)
    {
        float fameMid = (template.fameEffectMin + template.fameEffectMax) / 2f;
        float energyMid = (template.energyEffectMin + template.energyEffectMax) / 2f;
        ApplicaTemplateConValori(obj, template, fameMid, energyMid);
    }

    void ApplicaTemplateConValori(GameObject obj, InteractableTemplate template, float fameVal, float energyVal)
    {
        InteractableObject io = obj.GetComponent<InteractableObject>();
        if (io == null)
            io = obj.AddComponent<InteractableObject>();

        io.actionName = template.actionName;
        io.fameEffect = fameVal;
        io.energyEffect = energyVal;

        if (template.interactionAnimation != null)
        {
            io.interactionAnimation = template.interactionAnimation;
        }

        Transform pointEsistente = obj.transform.Find("InteractionPoint");
        if (pointEsistente == null)
        {
            GameObject point = new GameObject("InteractionPoint");
            point.transform.SetParent(obj.transform);
            point.transform.localPosition = template.interactionPointOffset;
            point.transform.localRotation = Quaternion.identity;
            io.interactionPoint = point.transform;
        }
        else
        {
            io.interactionPoint = pointEsistente;
        }

        Debug.Log($"[ANALIZZA SCENA] Applicato template '{template.nomeTemplate}' a '{obj.name}' (fame={fameVal}, energia={energyVal})");
    }

    void InviaAllLLM(List<GameObject> oggettiSconosciuti)
    {
        EditorCoroutine.Start(ChiamaServer(oggettiSconosciuti));
    }

IEnumerator ChiamaServer(List<GameObject> oggettiSconosciuti)
{
    List<Dictionary<string, string>> oggettiJson = new List<Dictionary<string, string>>();
    foreach (var obj in oggettiSconosciuti)
    {
        oggettiJson.Add(new Dictionary<string, string>
        {
            {"name", obj.name},
            {"tag", obj.tag}
        });
    }

    List<Dictionary<string, object>> templateJson = new List<Dictionary<string, object>>();
    foreach (var t in templates)
    {
        templateJson.Add(new Dictionary<string, object>
        {
            {"nome", t.nomeTemplate},
            {"azione", t.actionName},
            {"fame_min", t.fameEffectMin},
            {"fame_max", t.fameEffectMax},
            {"energia_min", t.energyEffectMin},
            {"energia_max", t.energyEffectMax}
        });
    }

    var payload = new
    {
        oggetti_sconosciuti = oggettiJson,
        template_disponibili = templateJson
    };
    string jsonBody = JsonConvert.SerializeObject(payload);

    using (UnityWebRequest request = new UnityWebRequest("http://127.0.0.1:8000/npc/analyze-scene", "POST"))
    {
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[ANALIZZA SCENA] Errore HTTP: {request.error}");
            Debug.LogError($"[ANALIZZA SCENA] Response code: {request.responseCode}");
            Debug.LogError($"[ANALIZZA SCENA] Body inviato: {jsonBody}");
            yield break;
        }

        string risposta = request.downloadHandler.text;
        Debug.Log($"[ANALIZZA SCENA] Risposta LLM: {risposta}");

        try
        {
            var wrapper = JsonConvert.DeserializeObject<Dictionary<string, object>>(risposta);
            var suggerimenti = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(wrapper["suggerimenti"].ToString());

            foreach (var sugg in suggerimenti)
            {
                string nomeOggetto = sugg["name"].ToString();
                string nomeTemplate = sugg["template"].ToString();

                if (nomeTemplate == "NonInteragibile")
                {
                    Debug.Log($"[ANALIZZA SCENA] '{nomeOggetto}' marcato come NonInteragibile, saltato.");
                    continue;
                }

                InteractableTemplate templateTrovato = null;
                foreach (var t in templates)
                {
                    if (t.nomeTemplate.ToLower() == nomeTemplate.ToLower())
                    {
                        templateTrovato = t;
                        break;
                    }
                }

                if (templateTrovato == null)
                {
                    Debug.LogWarning($"[ANALIZZA SCENA] Template '{nomeTemplate}' non trovato per '{nomeOggetto}'.");
                    continue;
                }

                float fameVal = Convert.ToSingle(sugg["fame_effect"]);
                float energyVal = Convert.ToSingle(sugg["energy_effect"]);

                GameObject obj = oggettiSconosciuti.Find(o => o.name == nomeOggetto);
                if (obj != null)
                {
                    ApplicaTemplateConValori(obj, templateTrovato, fameVal, energyVal);
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ANALIZZA SCENA] Errore parsing risposta: {e.Message}");
        }

        Debug.Log("[ANALIZZA SCENA] Completato.");
    }
}
    float EstraiValore(string json, string nomeOggetto, string chiave)
    {
        string pattern = $"\"{nomeOggetto}\"";
        int index = json.IndexOf(pattern);
        if (index < 0) return 0f;

        string sub = json.Substring(index);
        int keyIndex = sub.IndexOf($"\"{chiave}\"");
        if (keyIndex < 0) return 0f;

        string dopoChiave = sub.Substring(keyIndex + chiave.Length + 3);
        int virgola = dopoChiave.IndexOf(',');
        int graffa = dopoChiave.IndexOf('}');
        int fine = (virgola > 0 && virgola < graffa) ? virgola : graffa;
        if (fine < 0) fine = dopoChiave.Length;

        string valoreStr = dopoChiave.Substring(0, fine).Trim();
        float valore;
        if (float.TryParse(valoreStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out valore))
            return valore;

        return 0f;
    }

    void AggiungiNPC()
    {
        NPCController npcEsistente = FindObjectOfType<NPCController>();
        if (npcEsistente != null)
        {
            Debug.Log("[PREPARA SCENA] NPC gia' presente. Non lo aggiungo di nuovo.");
            return;
        }

        GameObject npcPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/NPC_AmbraIA.prefab");
        if (npcPrefab == null)
        {
            Debug.LogWarning("[PREPARA SCENA] Prefab NPC non trovato in Assets/Prefabs/NPC_AmbraIA.prefab.");
            return;
        }

        GameObject npc = (GameObject)PrefabUtility.InstantiatePrefab(npcPrefab);
        npc.transform.position = Vector3.zero;
        Debug.Log("[PREPARA SCENA] NPC aggiunto alla scena.");
    }

    void PreparaOggettiPerNavMesh()
    {
        GameObject[] tutti = FindObjectsOfType<GameObject>();
        int count = 0;
        foreach (GameObject obj in tutti)
        {
            if (obj.isStatic)
            {
                StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(obj);
                if ((flags & StaticEditorFlags.NavigationStatic) == 0)
                {
                    flags |= StaticEditorFlags.NavigationStatic;
                    GameObjectUtility.SetStaticEditorFlags(obj, flags);
                    count++;
                }
            }
        }
        Debug.Log($"[PREPARA SCENA] Navigation Static attivato su {count} oggetti.");
    }

    void BakeNavMesh()
    {
        NavMeshBuilder.BuildNavMesh();
        Debug.Log("[PREPARA SCENA] NavMesh calcolata.");
    }
}

public static class EditorCoroutine
{
    public static void Start(IEnumerator routine)
    {
        EditorCoroutineRunner runner = new GameObject("EditorCoroutineRunner").AddComponent<EditorCoroutineRunner>();
        runner.StartCoroutine(routine);
    }

private class EditorCoroutineRunner : MonoBehaviour
{
    void Awake()
    {
        hideFlags = HideFlags.HideAndDontSave;
        Destroy(gameObject, 30f);
    }
}
}
#endif