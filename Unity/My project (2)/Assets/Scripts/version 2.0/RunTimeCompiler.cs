using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

public class RuntimeCompiler : MonoBehaviour
{
    // Ora il metodo restituisce correttamente una stringa (vuota se OK, con l'errore se FALLITO)
    public string CompileAndAttachCode(string sourceCode, GameObject target)
    {
        Debug.Log("[COMPILER] Inizio compilazione del codice dinamico...");

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        List<MetadataReference> references = new List<MetadataReference>();
        foreach (Assembly loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!loadedAssembly.IsDynamic && !string.IsNullOrEmpty(loadedAssembly.Location))
            {
                references.Add(MetadataReference.CreateFromFile(loadedAssembly.Location));
            }
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            "GeneratedScript_" + Guid.NewGuid().ToString(),
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using (var ms = new MemoryStream())
        {
            EmitResult result = compilation.Emit(ms);

            if (!result.Success)
            {
                Debug.LogError("[COMPILER] Compilazione Fallita!");

                // 1. Raccogliamo tutti gli errori in una sola stringa da mandare a Python
                string erroriRilevati = "";
                foreach (Diagnostic diagnostic in result.Diagnostics)
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        erroriRilevati += $"{diagnostic.Id}: {diagnostic.GetMessage()}\n";
                        Debug.LogError($"Errore: {diagnostic.Id} - {diagnostic.GetMessage()}");
                    }
                }

                // 2. RITORNO AL MITTENTE: Spediamo gli errori all'Analyzer
                return erroriRilevati;
            }
            else
            {
                ms.Seek(0, SeekOrigin.Begin);
                Assembly assembly = Assembly.Load(ms.ToArray());

                Type typeToAttach = assembly.GetTypes().FirstOrDefault(t => t.BaseType != null && t.BaseType.Name == "MonoBehaviour");

                if (typeToAttach != null)
                {
                   
                    target.AddComponent(typeToAttach);

                    Debug.Log($"[COMPILER] Successo! Lo script '{typeToAttach.Name}' è stato attaccato a {target.name}.");
                    return "";
                }
                else
                {
                    Debug.LogWarning("[COMPILER] Compilazione ok, ma nessuna classe trovata.");
                    return "Errore: Nessuna classe che eredita da MonoBehaviour trovata nel codice.";
                }
            }
        }
    }

   
}