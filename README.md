# PropertyProxy
A simple way to create proxy objects

![Cheme](proxy.png)

✍✍✍ **Feedback is welcome** ✅✅✅

💚💚💚 **Stars are welcome** 💚💚💚

## Usage
Add `PropertyProxyFactory.cs` file to your project (.NET 9 or later)

Add `[PropertyProxy]` attribute to properties which you want to proxify
```csharp
class MyClass
{
    public int X { get; set; }

    [PropertyProxy]
    public int Y { get; set; }
}
```

You can create proxies for your MyClass's objects
```csharp
var myObject = new MyClass();
dynamic proxy = PropertyProxyFactory.Instance.CreateProxy(myObject);

// Set properties
proxy.Y = 10; // Has access! myObject.Y is 10 now
proxy.X = 20; // Error! RuntimeBinderException

// Get properties
Console.WriteLine($"Y: {proxy.Y}"); // Works good
Console.WriteLine($"X: {proxy.X}, Y: {proxy.Y}"); // Error! You have no access to property X

// Access through reflection
object proxy2 = PropertyProxyFactory.Instance.CreateProxy(myObject);
Type proxyType = proxy2.GetType();
foreach (PropertyInfo property in proxyType.GetProperties())
{
    string propertyName = property.Name;
    property.SetValue(proxy2, 20);
    string propertyValue = property.GetValue(proxy2)!.ToString()!;
    Console.WriteLine($"{propertyName} = {propertyValue}"); // You will see only "Y = 20"
}
```

A proxy has the same public accessors as the property: a property without a public setter
(`{ get; }`, `{ get; private set; }`, `{ get; init; }`) is read-only in the proxy too.

Using through interface (you don't need use attributes for this reason, and the class doesn't
have to implement the interface):
```csharp
class MyClass
{
    public int X { get; set; }
    public int Y { get; set; }
}

interface IMyInterface
{
    int Y { get; set; }
}

interface IReadOnlyView
{
    int Y { get; }
}

// ...

IMyInterface proxy3 = PropertyProxyFactory.Instance.CreateProxy<IMyInterface>(myObject);
proxy3.Y = 30; // Has access!

// The interface decides what is accessible: here Y can be read but not written
IReadOnlyView view = PropertyProxyFactory.Instance.CreateProxy<IReadOnlyView>(myObject);

// You can't get access to property X anyway
Console.WriteLine($"X = {proxy3.GetType().GetProperty("X")?.GetValue(proxy3)}"); // Nothing: there is no X
```
The class needs a public property of the same name and type for every property of the
interface (base interfaces included), with the accessors the interface declares; otherwise
`CreateProxy` throws an `ArgumentException`. Interfaces with methods or indexers can't be proxied.

Classes and interfaces don't have to be public. Proxies need a class: an object of a struct
would be copied into the proxy, so `CreateProxy` refuses it.

You can optimize proxy types generation by calling previously method `GenerateFor` with types or objects you want proxies for
```csharp
// For types:
PropertyProxyFactory.Instance.GenerateFor(typeof(MyClass1), typeof(MyClass2), typeof(MyClass3));

// For objects:
PropertyProxyFactory.Instance.GenerateFor(objectOfMyClass1, objectOfMyClass2, objectOfMyClass3);
```
When you call the `GenerateFor` method, one dynamic assembly is created for all the enumerated types or objects.
Every proxy type is generated once and cached; `PropertyProxyFactory` is thread-safe.

## License

The sources is available as open source under the terms of the [MIT License](http://opensource.org/licenses/MIT).
