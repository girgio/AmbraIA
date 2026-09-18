using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

/// <summary>
/// Compila codice C# a runtime con Roslyn e lo attacca al GameObject.
/// </summary>
public class RuntimeCompiler : MonoBehaviour
{
    /// <summary>
    /// Compila <paramref name="sourceCode"/> e attacca il MonoBehaviour risultante a <paramref name="target"/>.
    /// </summary>
    /// <returns>Stringa vuota se la compilazione è riuscita, messaggio di errore altrimenti.</returns>
    public string CompileAndAttachCode(string sourceCode, GameObject target)
    {
        Debug.Log("[COMPILER] Inizio compilazione del codice dinamico...");

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        // Raccoglie i riferimenti agli assembly caricati (esclude assembly dinamici senza path)
        List<MetadataReference> references = new List<MetadataReference>();
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location))
                references.Add(MetadataReference.CreateFromFile(asm.Location));
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            "GeneratedScript_" + Guid.NewGuid().ToString("N"),
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using (var ms = new MemoryStream())
        {
            EmitResult result = compilation.Emit(ms);

            if (!result.Success)
            {
                // Raccoglie solo gli errori (DiagnosticSeverity.Error), ignora i warning
                IEnumerable<Diagnostic> errors = result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error);

                string errorReport = string.Join("\n", errors.Select(d => $"{d.Id}: {d.GetMessage()}"));

                Debug.LogError($"[COMPILER] Compilazione fallita:\n{errorReport}");
                return errorReport;
            }

            ms.Seek(0, SeekOrigin.Begin);
            Assembly assembly = Assembly.Load(ms.ToArray());

            Type typeToAttach = assembly.GetTypes()
                .FirstOrDefault(t => t.BaseType != null && t.BaseType.Name == "MonoBehaviour");

            if (typeToAttach == null)
            {
                const string msg = "Nessuna classe che eredita da MonoBehaviour trovata nel codice generato.";
                Debug.LogWarning($"[COMPILER] {msg}");
                return msg;
            }

            // FIX: rimuovi il componente AiAction precedente per evitare accumulo
            RemovePreviousAiAction(target, typeToAttach.Name);

            target.AddComponent(typeToAttach);
            Debug.Log($"[COMPILER] Successo! '{typeToAttach.Name}' attaccato a '{target.name}'.");
            return "";
        }
    }

    /// <summary>
    /// Rimuove tutti i componenti con lo stesso nome della classe che stiamo per aggiungere,
    /// oppure qualsiasi componente chiamato "AiAction" (nome fisso del Builder).
    /// </summary>
    private void RemovePreviousAiAction(GameObject target, string newTypeName)
    {
        // Cerca componenti il cui tipo si chiama come il nuovo o come "AiAction"
        foreach (Component comp in target.GetComponents<Component>())
        {
            string compTypeName = comp.GetType().Name;
            if (compTypeName == newTypeName || compTypeName == "AiAction")
            {
                Debug.Log($"[COMPILER] Rimozione componente precedente: '{compTypeName}'");
                Destroy(comp);
            }
        }
    }
}