using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public class NPCController : MonoBehaviour
{
    private NavMeshAgent agent;
    private Animator animator;
    private AnimatorOverrideController overrideController;

    // Le tue statistiche
    public float fame = 0.7f;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        animator = GetComponent<Animator>();

       
        if (animator != null && animator.runtimeAnimatorController != null)
        {
            overrideController = new AnimatorOverrideController(animator.runtimeAnimatorController);
            animator.runtimeAnimatorController = overrideController;
        }
    }

    void Update()
    {
        if (agent != null && animator != null)
        {
            float currentSpeed = 0f;
            if (agent.speed > 0f)
            {
                currentSpeed = agent.velocity.magnitude / agent.speed;
            }
            if (currentSpeed < 0.01f) currentSpeed = 0f;

            animator.SetFloat("Speed", currentSpeed);
        }
    }

    // --- SKILL 1: MOVIMENTO ---
    public void MoveToTarget(string targetName)
    {
        GameObject targetObj = GameObject.Find(targetName);
        if (targetObj == null)
        {
            Debug.LogError($"[NPCController] Bersaglio '{targetName}' non trovato nella scena!");
            return;
        }

        UnityEngine.AI.NavMeshAgent agent = GetComponent<UnityEngine.AI.NavMeshAgent>();

       
        if (!agent.enabled)
        {
            agent.enabled = true;
        }

        
        if (!agent.isOnNavMesh)
        {
            UnityEngine.AI.NavMeshHit hit;
            // Cerca un punto valido sul pavimento azzurro entro 3 metri
            if (UnityEngine.AI.NavMesh.SamplePosition(transform.position, out hit, 3.0f, UnityEngine.AI.NavMesh.AllAreas))
            {
                agent.Warp(hit.position);
            }
        }

       
        if (agent.isOnNavMesh)
        {
            agent.SetDestination(targetObj.transform.position);
            Debug.Log($"[NPCController] In movimento verso: {targetName}");
        }
        else
        {
            Debug.LogError("[NPCController] Impossibile muoversi: l'NPC è completamente fuori dalla mappa NavMesh!");
        }
    }

    // --- NUOVA SKILL: INTERAZIONE DINAMICA CON OGGETTI ---
    public void InteractWith(string objectName, string actionName)
    {
        GameObject targetObj = GameObject.Find(objectName);
        if (targetObj == null) return;

        InteractableObject interactable = targetObj.GetComponent<InteractableObject>();
        if (interactable == null)
        {
            Debug.LogWarning($"L'oggetto {objectName} non ha il componente InteractableObject!");
            return;
        }

        if (interactable.actionName.ToLower() == actionName.ToLower())
        {
            StartCoroutine(ExecuteDynamicAction(interactable));
        }
    }

    private IEnumerator ExecuteDynamicAction(InteractableObject interactable)
    {
        if (agent == null || animator == null) yield break;

        // 1. Blocchiamo il movimento dell'agente, ma lo lasciamo ABILITATO (enabled = true)
        agent.isStopped = true;

        // 2. Lo "Snap" anticipato: Teletrasportiamo subito l'agente sul punto di interazione.
        // Usando Warp qui, la fisica della NavMesh si sposta insieme al modello 3D senza arrabbiarsi.
        if (interactable.interactionPoint != null)
        {
            agent.Warp(interactable.interactionPoint.position);
            transform.rotation = interactable.interactionPoint.rotation;
        }

        // 3. SOVRASCRITTURA ANIMAZIONE
        if (interactable.interactionAnimation != null && overrideController != null)
        {
            overrideController["Idle"] = interactable.interactionAnimation;
        }

        // 4. Avviamo l'animazione della sedia
        animator.SetTrigger("AvviaInterazione");
        Debug.Log($"[INTERAZIONE] Sto eseguendo l'azione '{interactable.actionName}' su {interactable.gameObject.name}");

        // Aspetta la fine dell'animazione
        yield return new WaitForSeconds(interactable.interactionAnimation.length);

        // 5. FINE ANIMAZIONE: Visto che l'agente non è mai stato spento, non c'è NESSUN teletrasporto fantasma!
        // Diciamo semplicemente all'NPC che è libero di camminare di nuovo se riceverà nuovi ordini.
        if (agent.isOnNavMesh)
        {
            agent.isStopped = false;
        }

        Debug.Log("[INTERAZIONE] Azione terminata. L'NPC è rimasto perfettamente sul posto.");
    }

    // --- SKILL 2: ANIMAZIONE VECCHIA (Mantenuta per compatibilità) ---
    public void Animate(string animationName)
    {
        if (animator != null)
        {
            Debug.Log($"[SKILL] Avvio animazione standard: {animationName}");
            animator.SetTrigger(animationName);
        }
    }

    // --- SKILL 3: MODIFICA STATISTICHE ---
    public void ModifyStat(string statName, float value)
    {
        if (statName.ToLower() == "fame")
        {
            fame = Mathf.Clamp01(fame + value);
            Debug.Log($"[SKILL] Nuova fame dell'NPC: {fame}");
        }
    }

    public Vector3 GetTargetPosition()
    {
        if (agent != null && agent.enabled && agent.hasPath)
        {
            return agent.destination;
        }
        return transform.position; // Se è fermo, restituisce la sua posizione attuale
    }

    // --- SE L'LLM CERCA UN METODO DI MOVIMENTO CHE DA' UN CONSENSO BOOL ---
    // Questa versione di sicurezza fa muovere l'NPC e restituisce 'true' se l'operazione è avviata
    public bool MoveToTargetWithCheck(string targetName)
    {
        MoveToTarget(targetName); // Chiama la tua skill 1 originale
        return agent != null && agent.enabled && agent.isOnNavMesh;
    }
}