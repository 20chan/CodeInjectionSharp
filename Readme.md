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
