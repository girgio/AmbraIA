using UnityEngine;

/// <summary>
/// Rappresenta un oggetto interagibile nella scena.
/// Identico alla versione originale, aggiunta solo l'auto-registrazione
/// nell'NPCController quando l'oggetto viene abilitato a runtime.
/// </summary>
public class InteractableObject : MonoBehaviour
{
    [Header("Interazione")]
    public string actionName = "Usa";
    public Transform interactionPoint;
    public AnimationClip interactionAnimation;

    [Header("Effetti Fisiologici (valore negativo = riduce il bisogno)")]
    public float fameEffect = 0f;      // es. -0.5 riduce la fame di 0.5
    public float energyEffect = 0f;    // es. -0.6 riduce la stanchezza di 0.
    
    public GameObject oggettoDaAttivare;

    public void DopoInterazione()
    {
        if (oggettoDaAttivare != null)
            oggettoDaAttivare.SetActive(true);
    }


    void OnEnable()
    {
        // Auto-registrazione: se un NPCController esiste nella scena,
        // questo oggetto si aggiunge al suo registro senza bisogno di RefreshInteractableRegistry()
        NPCController controller = FindObjectOfType<NPCController>();
        if (controller != null)
            controller.RegisterInteractable(this);
    }
}
