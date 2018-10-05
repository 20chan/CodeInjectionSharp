# CodeInjectionSharp

Inject machine code at machine code by hooking JIT at runtime

```csharp
static void Main(string[] args)
{
    Hook();
    Console.WriteLine(One());

    Console.Read();
}

static int One()
{
    return 1;
}
```

```
2
```

## How it works

Method `Hook()` replaces `ICorJitCompiler.compileMethod`. It makes we can view IL code of method and modify Jit-ed machine code at runtime, when method is called first time.

## References

https://www.codeproject.com/Articles/463508/NET-CLR-Injection-Modify-IL-Code-during-Run-time

http://xoofx.com/blog/2018/04/12/writing-managed-jit-in-csharp-with-coreclr/
