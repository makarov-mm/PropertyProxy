using System.Reflection;
using Microsoft.CSharp.RuntimeBinder;

PropertyProxyFactory factory = PropertyProxyFactory.Instance;

// Optional: generate the proxy types ahead of time, together in one dynamic assembly.
factory.GenerateFor(typeof(MyClass), typeof(MyClass1));

// A proxy with the [PropertyProxy] properties, used through dynamic.
var myObject = new MyClass { X = 1 };
dynamic proxy = factory.CreateProxy(myObject);
proxy.Y = 10;
Console.WriteLine($"proxy.Y = {proxy.Y}, myObject.Y = {myObject.Y}");
try
{
    proxy.X = 20;
}
catch (RuntimeBinderException)
{
    Console.WriteLine("proxy.X is out of reach");
}

// Reflection finds only the proxied properties.
object proxy2 = factory.CreateProxy(myObject);
foreach (PropertyInfo property in proxy2.GetType().GetProperties())
{
    property.SetValue(proxy2, 20);
    Console.WriteLine($"{property.Name} = {property.GetValue(proxy2)}");
}

// A proxy that implements an interface with the object's properties; MyClass3 doesn't implement it.
var myObject3 = new MyClass3 { X = 3, Y = 4 };
IMyClass3 proxy3 = factory.CreateProxy<IMyClass3>(myObject3);
myObject3.X = 30;
Console.WriteLine($"proxy3.X = {proxy3.X}");

// A read-only view: the interface has a getter only, and so has the proxy.
IReadOnlyMyClass3 view = factory.CreateProxy<IReadOnlyMyClass3>(myObject3);
Console.WriteLine($"view.X = {view.X}, writable: {view.GetType().GetProperty(nameof(view.X))!.CanWrite}");

class MyClass
{
    public int X { get; set; }

    [PropertyProxy]
    public int Y { get; set; }
}

class MyClass1
{
    public int X { get; set; }

    [PropertyProxy]
    public int Y { get; set; }
}

class MyClass3
{
    public int X { get; set; }

    public int Y { get; set; }
}

interface IMyClass3
{
    int X { get; set; }
}

interface IReadOnlyMyClass3
{
    int X { get; }
}
