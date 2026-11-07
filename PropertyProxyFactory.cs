using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

/// <summary>Marks a property that the proxies of <see cref="PropertyProxyFactory.CreateProxy(object)"/> expose.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PropertyProxyAttribute : Attribute;

/// <summary>
/// Creates proxies: objects that expose only some properties of another object, those marked with
/// <see cref="PropertyProxyAttribute"/> or those of an interface. A property of a proxy reads and
/// writes the property of the object, with the same public accessors; nothing else of the object
/// can be reached through the proxy.
/// </summary>
/// <remarks>
/// The proxy types are generated with Reflection.Emit, once per type of object (and interface), and
/// cached. Non-public types work too. The factory is thread-safe.
/// </remarks>
[RequiresDynamicCode("The proxy types are generated at run time with Reflection.Emit.")]
[RequiresUnreferencedCode("The proxies are made of the properties that reflection finds.")]
public sealed class PropertyProxyFactory
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
    private static readonly ConstructorInfo ObjectConstructor = typeof(object).GetConstructor(Type.EmptyTypes)!;
    private static readonly ConstructorInfo AttributeConstructor = typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!;
    private readonly ConcurrentDictionary<ProxyKey, Func<object, object>> _factories = new();
    private readonly Lock _generationLock = new();
    private int _assemblyCount;

    private PropertyProxyFactory() { }

    public static PropertyProxyFactory Instance { get; } = new();

    /// <summary>A proxy with the properties of <paramref name="source"/> that are marked [PropertyProxy], for use through dynamic or reflection.</summary>
    /// <exception cref="ArgumentException">The source is a value type.</exception>
    /// <exception cref="NotSupportedException">A marked property is static or an indexer.</exception>
    public object CreateProxy(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return FactoryFor(new ProxyKey(source.GetType(), null))(source);
    }

    /// <summary>
    /// A proxy that implements the interface <typeparamref name="T"/> with the public properties of
    /// <paramref name="source"/> of the same names and types; the source needn't implement it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// T is not an interface, the source is a value type, or it lacks a property or an accessor of the interface.
    /// </exception>
    /// <exception cref="NotSupportedException">The interface has members other than properties, or indexers.</exception>
    public T CreateProxy<T>(object source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        return (T)FactoryFor(new ProxyKey(source.GetType(), typeof(T)))(source);
    }

    /// <summary>Generates the [PropertyProxy] proxy types of these types ahead of time, in one dynamic assembly.</summary>
    public void GenerateFor(params ReadOnlySpan<Type> types)
    {
        List<ProxyKey> keys = new(types.Length);
        foreach (Type type in types)
            keys.Add(new ProxyKey(type ?? throw new ArgumentNullException(nameof(types)), null));
        Generate(keys);
    }

    /// <summary>
    /// Generates the [PropertyProxy] proxy types of the types of these objects ahead of time, in one
    /// dynamic assembly. A <see cref="Type"/> among them stands for itself.
    /// </summary>
    public void GenerateFor(params ReadOnlySpan<object> objects)
    {
        List<ProxyKey> keys = new(objects.Length);
        foreach (object item in objects)
            keys.Add(new ProxyKey(item as Type ?? item?.GetType() ?? throw new ArgumentNullException(nameof(objects)), null));
        Generate(keys);
    }

    private Func<object, object> FactoryFor(ProxyKey key)
    {
        if (!_factories.TryGetValue(key, out Func<object, object>? create))
        {
            Generate([key]);
            create = _factories[key];
        }
        return create;
    }

    // Generates the proxy types that don't exist yet, together in a new dynamic assembly. All of
    // them are checked before anything is emitted, so one bad type fails the whole batch.
    private void Generate(IEnumerable<ProxyKey> keys)
    {
        lock (_generationLock)
        {
            ProxyPlan[] plans = [.. keys.Distinct().Where(key => !_factories.ContainsKey(key)).Select(Plan)];
            if (plans.Length == 0)
                return;

            ModuleBuilder module = DefineModule(plans.SelectMany(plan => plan.AccessedAssemblies()));
            HashSet<string> names = [];
            foreach (ProxyPlan plan in plans)
                _factories[plan.Key] = Emit(module, plan, UniqueName(plan.Key, names));
        }
    }

    private readonly record struct ProxyKey(Type Source, Type? Interface);

    private sealed record ProxyPlan(ProxyKey Key, IReadOnlyList<ProxyProperty> Properties)
    {
        // The assemblies whose non-public types the proxy may refer to.
        public IEnumerable<Assembly> AccessedAssemblies()
        {
            IEnumerable<Type> types =
            [
                Key.Source,
                .. Key.Interface is { } @interface ? [@interface, .. @interface.GetInterfaces()] : Type.EmptyTypes,
                .. Properties.Select(p => p.Target.PropertyType),
                .. Properties.Select(p => p.Target.DeclaringType!),
            ];
            return types.SelectMany(AssembliesOf);
        }

        private static IEnumerable<Assembly> AssembliesOf(Type type) =>
            [
                type.Assembly,
                .. type.HasElementType ? AssembliesOf(type.GetElementType()!) : [],
                .. type.GenericTypeArguments.SelectMany(AssembliesOf),
            ];
    }

    /// <summary>A property of a proxy: the object's property, the accessors it forwards to, and the interface accessors it implements.</summary>
    private sealed class ProxyProperty(PropertyInfo target)
    {
        public PropertyInfo Target { get; } = target;
        public MethodInfo? Getter { get; set; }
        public MethodInfo? Setter { get; set; }
        public List<MethodInfo> ImplementedGetters { get; } = [];
        public List<MethodInfo> ImplementedSetters { get; } = [];
    }

    private static ProxyPlan Plan(ProxyKey key)
    {
        (Type source, Type? @interface) = key;

        if (source.IsValueType)
            throw new ArgumentException($"{source} is a value type: a proxy would hold a copy of the object, not the object.");

        if (source.ContainsGenericParameters)
            throw new ArgumentException($"{source} is an open generic type.");

        if (@interface is { IsInterface: false })
            throw new ArgumentException($"{@interface} is not an interface.");

        return new ProxyPlan(key, @interface is null ? MarkedProperties(source) : InterfaceProperties(source, @interface));
    }

    // The [PropertyProxy] properties with their public accessors; an init accessor doesn't count,
    // as it is meant for initialization only.
    private static List<ProxyProperty> MarkedProperties(Type source)
    {
        PropertyInfo[] marked =
        [
            .. source.GetProperties(PublicInstance | BindingFlags.Static).Where(p => Attribute.IsDefined(p, typeof(PropertyProxyAttribute))),
        ];

        if (marked.FirstOrDefault(p => (p.GetMethod ?? p.SetMethod)!.IsStatic || p.GetIndexParameters().Length > 0) is { } unsupported)
            throw new NotSupportedException($"{source}.{unsupported.Name} can't be proxied: only instance properties without parameters can.");

        return
        [
            .. marked.GroupBy(p => p.Name).Select(group => MostDerived(group)!).Select(target => new ProxyProperty(target)
            {
                Getter = target.GetGetMethod(),
                Setter = target.GetSetMethod() is { } setter && !IsInitOnly(setter) ? setter : null,
            }),
        ];
    }

    // The abstract properties of the interface and of its base interfaces, each implemented with
    // the public property of the source of the same name and type.
    private static List<ProxyProperty> InterfaceProperties(Type source, Type @interface)
    {
        Dictionary<string, ProxyProperty> properties = [];
        foreach (Type declaringInterface in (Type[])[@interface, .. @interface.GetInterfaces()])
        {
            HashSet<MethodInfo> accessors = [.. declaringInterface.GetProperties().SelectMany(p => p.GetAccessors())];

            if (declaringInterface.GetMethods().FirstOrDefault(m => m.IsAbstract && !accessors.Contains(m)) is { } member)
                throw new NotSupportedException($"{declaringInterface}.{member.Name} can't be implemented: proxies implement properties only.");

            foreach (PropertyInfo declaration in declaringInterface.GetProperties())
            {
                MethodInfo? getter = declaration.GetMethod is { IsAbstract: true } g ? g : null;
                MethodInfo? setter = declaration.SetMethod is { IsAbstract: true } s ? s : null;

                if (getter is null && setter is null)
                    continue; // implemented by the interface itself

                if (declaration.GetIndexParameters().Length > 0)
                    throw new NotSupportedException($"{declaringInterface}.{declaration.Name} can't be implemented: proxies don't support indexers.");

                string needs = $"which {declaringInterface} needs";
                if (!properties.TryGetValue(declaration.Name, out ProxyProperty? property))
                {
                    PropertyInfo target = MostDerived(source.GetProperties(PublicInstance).Where(p => p.Name == declaration.Name && p.GetIndexParameters().Length == 0))
                        ?? throw new ArgumentException($"{source} has no public property {declaration.Name}, {needs}.");

                    properties.Add(declaration.Name, property = new ProxyProperty(target));
                }

                if (property.Target.PropertyType != declaration.PropertyType)
                    throw new ArgumentException($"{source}.{declaration.Name} is {property.Target.PropertyType}, but {declaringInterface} needs {declaration.PropertyType}.");

                if (getter is not null)
                {
                    property.Getter = property.Target.GetGetMethod()
                        ?? throw new ArgumentException($"{source}.{declaration.Name} has no public getter, {needs}.");

                    property.ImplementedGetters.Add(getter);
                }

                if (setter is not null)
                {
                    // An init accessor of the interface may forward to any setter, a set accessor only to a set accessor.
                    MethodInfo? targetSetter = property.Target.GetSetMethod();

                    if (targetSetter is null || (IsInitOnly(targetSetter) && !IsInitOnly(setter)))
                        throw new ArgumentException($"{source}.{declaration.Name} has no public setter, {needs}.");

                    property.Setter = targetSetter;
                    property.ImplementedSetters.Add(setter);
                }
            }
        }

        return [.. properties.Values];
    }

    // Of properties with one name, the one declared furthest down the class hierarchy: what C# binds to.
    private static PropertyInfo? MostDerived(IEnumerable<PropertyInfo> properties) =>
        properties.MaxBy(p => Depth(p.DeclaringType!));

    private static int Depth(Type type)
    {
        int depth = 0;

        for (Type? t = type.BaseType; t is not null; t = t.BaseType)
            depth++;

        return depth;
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));

    private ModuleBuilder DefineModule(IEnumerable<Assembly> accessedAssemblies)
    {
        var name = new AssemblyName("PropertyProxies" + (++_assemblyCount).ToString(CultureInfo.InvariantCulture));
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.RunAndCollect);
        ModuleBuilder module = assembly.DefineDynamicModule(name.Name!);

        // For proxies of non-public types: the runtime skips its access checks for the assemblies
        // that an IgnoresAccessChecksToAttribute of the accessing assembly names. It recognizes the
        // attribute by its name, and no library defines it publicly, so each proxy assembly has its own.
        TypeBuilder attribute = module.DefineType(
            "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(Attribute));

        ConstructorBuilder constructor = attribute.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(string)]);
        ILGenerator il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, AttributeConstructor);
        il.Emit(OpCodes.Ret);
        ConstructorInfo ignoresAccessChecksTo = attribute.CreateType().GetConstructor([typeof(string)])!;

        foreach (string accessed in accessedAssemblies.Select(a => a.GetName().Name!).Distinct())
            assembly.SetCustomAttribute(new CustomAttributeBuilder(ignoresAccessChecksTo, [accessed]));

        return module;
    }

    // PropertyProxies.MyClassProxy, or PropertyProxies.MyClassAsIMyInterface for an interface proxy.
    private static string UniqueName(ProxyKey key, HashSet<string> taken)
    {
        static string Plain(Type type) => type.Name.Split('`')[0];
        string name = "PropertyProxies." + Plain(key.Source) + (key.Interface is null ? "Proxy" : "As" + Plain(key.Interface));
        string unique = name;

        for (int i = 2; !taken.Add(unique); i++)
            unique = name + i.ToString(CultureInfo.InvariantCulture);

        return unique;
    }

    /// <summary>
    /// Emits the proxy type and gives its factory:
    /// <code>
    /// public sealed class MyClassProxy [: IMyInterface]
    /// {
    ///     private readonly MyClass _owner;
    ///     private MyClassProxy(MyClass owner) => _owner = owner;
    ///     public int Y { get => _owner.Y; set => _owner.Y = value; }
    ///     private static object Create(object source) => new MyClassProxy((MyClass)source);
    /// }
    /// </code>
    /// </summary>
    private static Func<object, object> Emit(ModuleBuilder module, ProxyPlan plan, string name)
    {
        (Type source, Type? @interface) = plan.Key;
        TypeBuilder type = module.DefineType(
            name,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(object),
            @interface is null ? Type.EmptyTypes : [@interface]);

        FieldBuilder owner = type.DefineField("_owner", source, FieldAttributes.Private | FieldAttributes.InitOnly);

        ConstructorBuilder constructor = type.DefineConstructor(MethodAttributes.Private | MethodAttributes.HideBySig, CallingConventions.Standard, [source]);
        ILGenerator il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, ObjectConstructor);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, owner);
        il.Emit(OpCodes.Ret);

        MethodAttributes accessor = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName;

        if (@interface is not null)
            accessor |= MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;

        foreach (ProxyProperty property in plan.Properties)
            EmitProperty(type, owner, property, accessor);

        // The cast keeps a proxy from ever holding an object of another type.
        MethodBuilder create = type.DefineMethod("Create", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, typeof(object), [typeof(object)]);
        il = create.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, source);
        il.Emit(OpCodes.Newobj, constructor);
        il.Emit(OpCodes.Ret);

        return type.CreateType()
            .GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<object, object>>();
    }

    private static void EmitProperty(TypeBuilder type, FieldInfo owner, ProxyProperty property, MethodAttributes accessor)
    {
        PropertyInfo target = property.Target;
        PropertyBuilder proxy = type.DefineProperty(target.Name, PropertyAttributes.None, target.PropertyType, null);

        if (property.Getter is { } getter)
        {
            MethodBuilder method = type.DefineMethod("get_" + target.Name, accessor, target.PropertyType, Type.EmptyTypes);
            EmitForwarding(method, owner, getter, hasValue: false);
            proxy.SetGetMethod(method);

            foreach (MethodInfo declaration in property.ImplementedGetters)
                type.DefineMethodOverride(method, declaration);
        }

        if (property.Setter is { } setter)
        {
            // An init accessor's signature carries a modreq, which the implementation has to repeat.
            Type[]? modifiers = property.ImplementedSetters.Any(IsInitOnly) ? [typeof(IsExternalInit)] : null;
            MethodBuilder method = type.DefineMethod(
                "set_" + target.Name, accessor, CallingConventions.Standard,
                typeof(void), modifiers, null, [target.PropertyType], null, null);
            EmitForwarding(method, owner, setter, hasValue: true);
            proxy.SetSetMethod(method);

            foreach (MethodInfo declaration in property.ImplementedSetters)
                type.DefineMethodOverride(method, declaration);
        }
    }

    // _owner.Accessor() or _owner.Accessor(value)
    private static void EmitForwarding(MethodBuilder method, FieldInfo owner, MethodInfo accessor, bool hasValue)
    {
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, owner);
        if (hasValue)
            il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, accessor);
        il.Emit(OpCodes.Ret);
    }
}
