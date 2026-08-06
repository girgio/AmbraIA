using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using UnityEngine.Networking;
using System.Collections;

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
                GUILayout.Label($"- {t.nomeTemplate} (azione: {t.actionName}, fame: [{t.fameEffectMin}..{t.fameEffectMax}], energy: [{t.energyEffectMin}..{t.energyEffectMax}])");
            }
            GUILayout.EndScrollView();
        }

        GUILayout.Space(20);

        if (GUILayout.Button("Analizza Scena e Applica InteractableObject", GUILayout.Height(40)))
        {
            if (templates == null || templates.Length == 0)
            {
                Debug.LogError("Nessun template trovato. Crea almeno un InteractableTemplate in Assets.");
                return;
            }
            AnalizzaScena();
        }
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
        Debug.Log($"[ANALIZZA SCENA] Da inviare all'LLM per template + valori: {daAnalizzare.Count} oggetti");

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
            Debug.Log("[ANALIZZA SCENA] Tutti gli oggetti sono stati matchati automaticamente con valori medi.");
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

        Debug.Log($"[ANALIZZA SCENA] Applicato template '{template.nomeTemplate}' a '{obj.name}' (fame={fameVal}, energy={energyVal})");
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
                {"energy_min", t.energyEffectMin},
                {"energy_max", t.energyEffectMax}
            });
        }

        string jsonBody = JsonUtility.ToJson(new
        {
            oggetti_sconosciuti = oggettiJson,
            template_disponibili = templateJson
        });

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
                yield break;
            }

            string risposta = request.downloadHandler.text;
            Debug.Log($"[ANALIZZA SCENA] Risposta LLM: {risposta}");

            foreach (var obj in oggettiSconosciuti)
            {
                foreach (var t in templates)
                {
                    if (risposta.Contains($"\"{obj.name}\"") && risposta.Contains($"\"{t.nomeTemplate}\""))
                    {
                        float fameVal = EstraiValore(risposta, obj.name, "fame_effect");
                        float energyVal = EstraiValore(risposta, obj.name, "energy_effect");

                        if (fameVal == 0f && energyVal == 0f)
                        {
                            fameVal = (t.fameEffectMin + t.fameEffectMax) / 2f;
                            energyVal = (t.energyEffectMin + t.energyEffectMax) / 2f;
                        }

                        ApplicaTemplateConValori(obj, t, fameVal, energyVal);
                        break;
                    }
                }
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
        void Awake() { hideFlags = HideFlags.HideAndDontSave; }
    }
}
#endif