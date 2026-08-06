using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class NPCController : MonoBehaviour
{
    [Header("Statistiche NPC (0 = OK, 1 = Bisogno Massimo)")]
    [Range(0f, 1f)] public float fame = 0.3f;
    [Range(0f, 1f)] public float energy = 0.3f;

    [Header("Configurazione Fisiologica")]
    public float consumoFamePassivo = 0.0007f;
    public float consumoEnergiaMovimento = 0.007f;
    public float consumoEnergiaIdle = 0.0004f;
    public float sogliaCritica = 0.7f;
    public float cooldownPianificazione = 3.0f;

    [Header("Configurazione Interazione")]
    public float navMeshReanchorRadius = 2.0f;

    private NavMeshAgent _agent;
    private Animator _animator;
    private AnimatorOverrideController _overrideController;
    private Dictionary<string, GameObject> _interactableRegistry = new Dictionary<string, GameObject>();
    private SceneAnalyzer _analyzer;

    private float _lastPlanningTime = -999f;
    private bool _isPerformingAction = false;

    void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _animator = GetComponent<Animator>();
        _analyzer = GetComponent<SceneAnalyzer>();

        if (_animator != null && _animator.runtimeAnimatorController != null)
        {
            _overrideController = new AnimatorOverrideController(_animator.runtimeAnimatorController);
            _animator.runtimeAnimatorController = _overrideController;
        }
    }

    void Start()
    {
        RefreshInteractableRegistry();
        TriggerPlanning();
    }

    void Update()
    {
        if (_agent != null && _animator != null)
        {
            float speed = _agent.speed > 0f ? _agent.velocity.magnitude / _agent.speed : 0f;
            _animator.SetFloat("Speed", speed < 0.01f ? 0f : speed);
            SimulatePhysiology(speed > 0.1f);
        }

        if (!_isPerformingAction && Time.time > _lastPlanningTime + cooldownPianificazione)
        {
            if (_analyzer != null && _analyzer.IsRequestInProgress)
                return;

            bool bisognoCritico = fame >= sogliaCritica || energy >= sogliaCritica;
            bool nessunPianoAttivo = !_isPerformingAction;

            if (nessunPianoAttivo)
            {
                TriggerPlanning();
            }
        }
    }

    private void SimulatePhysiology(bool isMoving)
    {

        fame = Mathf.Clamp01(fame + (consumoFamePassivo * Time.deltaTime));
        float incrementoStanchezza = isMoving ? consumoEnergiaMovimento : consumoEnergiaIdle;
        energy = Mathf.Clamp01(energy + (incrementoStanchezza * Time.deltaTime));
     
    }

    public void TriggerPlanning()
    {
        if (_analyzer != null)
        {
            Debug.Log($"[NPCController] Richiesta pianificazione (Fame: {fame:F2}, Stanchezza: {energy:F2})");
            _lastPlanningTime = Time.time;
            _analyzer.TriggerPlanning();
        }
    }

    public void RefreshInteractableRegistry()
    {
        _interactableRegistry.Clear();
        foreach (var interactable in FindObjectsOfType<InteractableObject>())
        {
            string key = interactable.gameObject.name;
            if (!_interactableRegistry.ContainsKey(key))
                _interactableRegistry[key] = interactable.gameObject;
        }
        Debug.Log($"[NPCController] Registro aggiornato: {_interactableRegistry.Count} oggetti.");
    }

    public void RegisterInteractable(InteractableObject obj)
    {
        string key = obj.gameObject.name;
        if (!_interactableRegistry.ContainsKey(key))
            _interactableRegistry[key] = obj.gameObject;
    }

    private GameObject FindRegisteredObject(string targetName)
    {
        if (_interactableRegistry.TryGetValue(targetName, out GameObject found))
            return found;

        GameObject fallback = GameObject.Find(targetName);
        if (fallback == null)
            Debug.LogError($"[NPCController] Oggetto '{targetName}' non trovato.");
        return fallback;
    }

    public IEnumerator MoveToTargetAndWait(string targetName)
    {
        _isPerformingAction = true;

        if (_analyzer != null)
        {
            string record = $"Mi sono spostato verso {targetName}";
            _analyzer.lastActions.Add(record);
            if (_analyzer.lastActions.Count > 10)
                _analyzer.lastActions.RemoveAt(0);

            _analyzer.NotifyActionDone(record);
        }

        GameObject targetObj = FindRegisteredObject(targetName);

        if (targetObj != null)
        {
            if (!_agent.enabled) _agent.enabled = true;
            _agent.SetDestination(targetObj.transform.position);

            while (_agent.pathPending || (_agent.remainingDistance > _agent.stoppingDistance))
            {
                yield return null;
            }
        }
        _isPerformingAction = false;
    }

    public IEnumerator InteractWithAndWait(string objectName, string actionName)
    {
        _isPerformingAction = true;
        GameObject targetObj = FindRegisteredObject(objectName);

        if (targetObj != null)
        {
            InteractableObject interactable = targetObj.GetComponent<InteractableObject>();
            if (interactable != null && string.Equals(interactable.actionName, actionName, System.StringComparison.OrdinalIgnoreCase))
            {
                if (_analyzer != null)
                {
                    string record = $"{actionName} su {objectName}";
                    _analyzer.lastActions.Add(record);
                    if (_analyzer.lastActions.Count > 10)
                        _analyzer.lastActions.RemoveAt(0);

                    _analyzer.NotifyActionDone(record);
                }

                yield return StartCoroutine(ExecuteDynamicAction(interactable));
            }
        }
        _isPerformingAction = false;
    }

    public void InteractWith(string objectName, string actionName)
    {
        StartCoroutine(InteractWithAndWait(objectName, actionName));
    }

    private IEnumerator ExecuteDynamicAction(InteractableObject interactable)
    {
        if (_agent == null || _animator == null) yield break;

        if (_agent.enabled && _agent.isOnNavMesh)
            _agent.isStopped = true;

        _agent.enabled = false;

        if (interactable.interactionPoint != null)
        {
            transform.position = interactable.interactionPoint.position;
            transform.rotation = interactable.interactionPoint.rotation;
        }

        if (interactable.interactionAnimation != null && _overrideController != null)
            _overrideController["Idle"] = interactable.interactionAnimation;

        _animator.SetTrigger("AvviaInterazione");
        Debug.Log($"[NPCController] Interazione: '{interactable.actionName}' su {interactable.gameObject.name}");

        float duration = interactable.interactionAnimation != null ? interactable.interactionAnimation.length : 2.0f;
        yield return new WaitForSeconds(duration);

        ApplyInteractionEffects(interactable);
        interactable.DopoInterazione();

        _agent.enabled = true;
        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, navMeshReanchorRadius, NavMesh.AllAreas))
        {
            _agent.Warp(hit.position);
        }
        else
        {
            Debug.LogWarning($"[NPCController] Nessun punto NavMesh valido dopo '{interactable.gameObject.name}'.");
        }

        if (_agent.isOnNavMesh)
            _agent.isStopped = false;

        Debug.Log("[NPCController] Azione completata.");
    }

    private void ApplyInteractionEffects(InteractableObject interactable)
    {
        if (interactable.fameEffect != 0f)
        {
            ModifyStat("fame", interactable.fameEffect);
        }

        if (interactable.energyEffect != 0f)
        {
            ModifyStat("energy", interactable.energyEffect);
        }
    }

    public void ModifyStat(string statName, float value)
    {
        switch (statName.ToLower())
        {
            case "fame":
                fame = Mathf.Clamp01(fame + value);
                Debug.Log($"[NPCController] Fame: {fame:F2}");
                break;
            case "energy":
            case "energia":
                energy = Mathf.Clamp01(energy + value);
                Debug.Log($"[NPCController] Stanchezza: {energy:F2}");
                break;
        }
    }

    public Vector3 GetTargetPosition()
    {
        if (_agent != null && _agent.enabled && _agent.hasPath)
            return _agent.destination;
        return transform.position;
    }

    public float GetFame()
    {
        return fame;
    }

    public float GetEnergy()
    {
        return energy;
    }
}