using UnityEngine;

public class InteractableObject : MonoBehaviour
{
    [Header("Configurazione per l'IA")]
    public string actionName;          // Il nome dell'azione (es: "Siediti", "Bevi")

    [Header("Allineamento e Animazione")]
    public Transform interactionPoint; // L'oggetto vuoto (figlio) dove l'NPC deve fare lo "snap"
    public AnimationClip interactionAnimation; // Il file dell'animazione specifico di QUESTO oggetto
}
