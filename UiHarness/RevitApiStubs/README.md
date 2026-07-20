# Revit API runtime stubs for UiHarness

`RevitAPI.dll` / `RevitAPIUI.dll` here are **metadata-only stubs** copied next
to `UiHarness.exe` so the CLR can *load and JIT* the addin's Revit-touching
code without a Revit install. Calling any Revit API member still throws —
that's the harness contract (UI look/layout only, see
`docs/ui-development-without-revit.md`).

## Why stubs are needed at all

The Nice3point NuGet packages ship the **real mixed-mode (C++/CLI) Revit
DLLs** as compile-time refs. Those cannot load at runtime without Revit's
native DLLs. And plain `refasmer` output of them is *also* unloadable, for
several reasons discovered the hard way (see below).

## Regenerating (when the Revit API package major version bumps)

1. `dotnet tool install -g JetBrains.Refasmer.CliTool`
2. From the package ref DLL, produce a raw ref assembly (mock mode `-m`
   overflows the UserString heap on RevitAPI, so use plain ref mode):

   ```
   refasmer -n --all -O out %USERPROFILE%\.nuget\packages\nice3point.revit.api.revitapi\<ver>\ref\net8.0-windows7.0\RevitAPI.dll
   refasmer -n --all -O out %USERPROFILE%\.nuget\packages\nice3point.revit.api.revitapiui\<ver>\ref\net8.0-windows7.0\RevitAPIUI.dll
   ```

3. Post-process with the Mono.Cecil program below. It fixes what refasmer
   leaves broken for mixed-mode assemblies:
   - **deletes all `<Module>` global methods/fields** (C++/CLI native cruft;
     CoreCLR loads the global type before any real type, and its broken
     records made *every* type load fail with `COMException 0x80131130
     "Record not found on lookup"`)
   - **rewrites every method body** to `ldnull; throw` and clears
     native/pinvoke impl flags (ref-mode output keeps C++/CLI native entry
     points → `TypeLoadException: Bad unmanaged code entry point`)
   - **static ctors get an empty `ret` body** (they run implicitly on first
     type use; a throwing cctor breaks the type forever)
   - **delegate methods stay runtime-implemented with no body**
   - **clears orphaned `HasSecurity` flags** (declarations were stripped but
     the flag survived) and **dangling `FieldRVA` flags**

```csharp
// dotnet add package Mono.Cecil
using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class StubFixer
{
    static int Main(string[] args) // <input.dll> <output.dll>
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(args[0])));
        var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });

        foreach (var module in asm.Modules)
        {
            var global = module.GetType("<Module>");
            global.Methods.Clear();
            global.Fields.Clear();

            foreach (var type in module.GetTypes())
            {
                if ((type.Attributes & TypeAttributes.HasSecurity) != 0 && !type.HasSecurityDeclarations)
                    type.Attributes &= ~TypeAttributes.HasSecurity;

                bool isDelegate = type.BaseType != null &&
                    (type.BaseType.FullName == "System.MulticastDelegate" || type.BaseType.FullName == "System.Delegate");

                foreach (var f in type.Fields)
                    if ((f.Attributes & FieldAttributes.HasFieldRVA) != 0 && (f.InitialValue == null || f.InitialValue.Length == 0))
                        f.Attributes &= ~FieldAttributes.HasFieldRVA;

                foreach (var m in type.Methods)
                {
                    if ((m.Attributes & MethodAttributes.HasSecurity) != 0 && !m.HasSecurityDeclarations)
                        m.Attributes &= ~MethodAttributes.HasSecurity;
                    if (m.HasPInvokeInfo || m.IsPInvokeImpl) { m.PInvokeInfo = null; m.IsPInvokeImpl = false; }

                    if (isDelegate && !m.IsStatic)
                    {
                        m.Body = null;
                        m.ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed;
                        continue;
                    }
                    if (m.IsAbstract) { m.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed; continue; }

                    m.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
                    var body = new MethodBody(m);
                    var il = body.GetILProcessor();
                    if (m.IsStatic && m.Name == ".cctor")
                        il.Append(il.Create(OpCodes.Ret));
                    else { il.Append(il.Create(OpCodes.Ldnull)); il.Append(il.Create(OpCodes.Throw)); }
                    m.Body = body;
                }
            }
        }
        asm.Write(args[1]);
        return 0;
    }
}
```

4. Replace the DLLs in this folder with the post-processed output.

## Runtime resolution

Loose DLLs next to the exe are **not** in `UiHarness.deps.json`, and the .NET
host only binds assemblies listed there. `UiHarness/App.xaml.cs` registers an
`AssemblyLoadContext.Default.Resolving` hook that loads them from the app
folder — without it every Revit-touching window ctor dies with
`FileNotFoundException 'RevitAPIUI'`.
