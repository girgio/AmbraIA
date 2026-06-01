using UnityEngine;

public class AiAction : MonoBehaviour
{
    void Start()
    {
        // Ruota l'NPC di 90 gradi verso destra
        transform.Rotate(Vector3.forward, -90f);
    }
}