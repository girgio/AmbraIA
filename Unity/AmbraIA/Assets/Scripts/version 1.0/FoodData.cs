using UnityEngine;

public class FoodData : MonoBehaviour
{
    [Range(0f, 1f)]
    [Tooltip("Quanto riduce la fame quando viene mangiato (0=nulla, 1=molta)")]
    public float valoreNutritivo = 0.5f;
}
