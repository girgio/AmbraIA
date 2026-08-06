using UnityEngine;
using System.Collections.Generic;

[CreateAssetMenu(fileName = "NuovoTemplate", menuName = "NPC/Template Interagibile")]
public class InteractableTemplate : ScriptableObject
{
    [Header("Identificazione")]
    public string nomeTemplate = "NuovoTemplate";

    [Header("Configurazione InteractableObject")]
    public string actionName = "Usa";

    [Header("Range Effetti (l'LLM scegliera' un valore tra min e max)")]
    [Range(-1f, 1f)] public float fameEffectMin = 0f;
    [Range(-1f, 1f)] public float fameEffectMax = 0f;
    [Range(-1f, 1f)] public float energyEffectMin = 0f;
    [Range(-1f, 1f)] public float energyEffectMax = 0f;

    [Header("Animazione (opzionale)")]
    public AnimationClip interactionAnimation;

    [Header("Interaction Point Automatico")]
    [Tooltip("Offset rispetto al centro dell'oggetto. Z negativo = davanti all'oggetto.")]
    public Vector3 interactionPointOffset = new Vector3(0, 0, -1f);

    [Header("Regole di Matching Automatico")]
    [Tooltip("Parole da cercare nel nome dell'oggetto (es. 'mela', 'sedia')")]
    public List<string> paroleChiaveNome = new List<string>();

    [Tooltip("Tag che attivano questo template (es. 'Food', 'Furniture')")]
    public List<string> tags = new List<string>();

    public bool Matcha(GameObject oggetto)
    {
        string nomeLower = oggetto.name.ToLower();
        string tagLower = oggetto.tag.ToLower();

        foreach (string parola in paroleChiaveNome)
        {
            if (nomeLower.Contains(parola.ToLower()))
                return true;
        }

        foreach (string tag in tags)
        {
            if (tagLower == tag.ToLower())
                return true;
        }

        return false;
    }
}