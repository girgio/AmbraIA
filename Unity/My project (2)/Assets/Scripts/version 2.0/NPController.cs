using UnityEngine;
using UnityEngine.AI; // Se usi il NavMesh per muoverti

public class NPCController : MonoBehaviour
{
    private NavMeshAgent agent;
    private Animator animator;

    // Le tue statistiche
    public float fame = 0.7f;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        animator = GetComponent<Animator>();
    }

    // --- SKILL 1: MOVIMENTO (Implementazione reale) ---
    public void MoveToTarget(string objectName)
    {
        GameObject target = GameObject.Find(objectName);
        if (target != null && agent != null)
        {
            Debug.Log($"[SKILL] Mi muovo verso: {objectName}");
            agent.SetDestination(target.transform.position);
        }
        else
        {
            Debug.LogWarning($"Target {objectName} non trovato nella scena!");
        }
    }

    // --- SKILL 2: ANIMAZIONE (Implementazione reale) ---
    public void Animate(string animationName)
    {
        if (animator != null)
        {
            Debug.Log($"[SKILL] Avvio animazione: {animationName}");
            animator.SetTrigger(animationName); // Es. trigger "Pickup" o "Eat"
        }
    }

    // --- SKILL 3: MODIFICA STATISTICHE (Implementazione reale) ---
    public void ModifyStat(string statName, float value)
    {
        if (statName.ToLower() == "fame")
        {
            fame = Mathf.Clamp01(fame + value);
            Debug.Log($"[SKILL] Nuova fame dell'NPC: {fame}");
        }
    }
}