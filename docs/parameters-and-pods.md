# Parameters and SPA pods

Everything configurable on a node, port or device is a **parameter**, and every parameter is a
**pod**: SPA's self-describing binary format. Volumes, channel maps, formats, card profiles and
routes all arrive as pods, so reading one is the difference between "I got a `SpaObject` back" and
"I can use it".

The convenience wrappers cover the common cases - `GetVolumeAsync`, `SetMutedAsync`,
`EnumerateRoutesAsync` - and this page is for when you need what they do not wrap.

## The shape of a pod

A pod is one of a closed set of values. The ones you meet:

| Pod | C# | Holds |
|---|---|---|
| Int, Long, Float, Double, Bool, Id | `SpaInt`, `SpaLong`, `SpaFloat`, `SpaDouble`, `SpaBool`, `SpaId` | a scalar; `SpaId` is an enum value |
| String, Bytes | `SpaString`, `SpaBytes` | text, raw bytes |
| Rectangle, Fraction | `SpaRectangle`, `SpaFraction` | a size, a rate |
| Array, Struct | `SpaArray`, `SpaStruct` | same-typed list, mixed list |
| **Object** | `SpaObject` | a set of keyed properties - this is what a parameter is |
| **Choice** | `SpaChoice` | "one of", "a range of", "in steps of" - how negotiation is expressed |

`SpaValue` is the base of all of them, so a parameter tree is `SpaValue` all the way down and you
pattern-match your way in.

## Reading a parameter

A parameter is an object whose properties are keyed by an enum from the family that parameter
belongs to (`SpaParamRoute`, `SpaProp`, `SpaFormat`, ...). Index it by that enum directly - `SpaKey`
converts from every family implicitly, so there is no cast - and match the value:

```csharp
await using PipeWireDeviceProxy device = registry.BindDevice(deviceId);
await device.ReadyAsync(cancellationToken);

foreach (SpaObject route in await device.EnumerateRoutesAsync(cancellationToken))
{
    string? name = route[SpaParamRoute.Name] is SpaString s ? s.Value : null;
    int index = route[SpaParamRoute.Index] is SpaInt i ? i.Value : -1;
    var available = route[SpaParamRoute.Available] is SpaId a
        ? (SpaParamAvailability)a.Value
        : SpaParamAvailability.Unknown;

    Console.WriteLine($"route {index} {name} available={available}");
}
```

Three habits worth forming, all visible above:

- **Use the family's own enum as the key.** `route[SpaParamRoute.Name]` rather than a bare number:
  the enum says which parameter family the key belongs to, and mixing families is the mistake it
  prevents.
- **Match, do not cast.** A property that is absent indexes to null, and one the daemon sent as a
  different type is a legitimate thing to skip rather than throw on.
- **`SpaId` is an enum in disguise.** Its `Value` is a `uint` you cast to the enum the parameter's
  family defines - `SpaParamAvailability` here.

`route.Properties` is the whole set if you would rather enumerate than look keys up, and
`ToString()` on any pod prints a readable tree, which is the fastest way to see what a device
actually sent you.

## Choices: what negotiation looks like

A value the peer has not settled yet arrives as `SpaChoice`. That is how a format says "any size
between these two" or "one of these three formats".

```csharp
static string Describe(SpaValue value) => value switch
{
    SpaChoice { Kind: SpaChoiceType.Range, Alternatives.Length: >= 3 } c
        => $"{c.Alternatives[1]} to {c.Alternatives[2]} (default {c.Default})",
    SpaChoice { Kind: SpaChoiceType.Enum } c
        => string.Join(" | ", c.Alternatives),
    _ => value.ToString() ?? "",
};
```

For a `Range` the alternatives are default, minimum, maximum in that order; for an `Enum` they are
the permitted values, with `Default` repeated first. `SpaChoice.Default` is what the peer would pick
if you expressed no preference.

## Writing a parameter

Build the object and hand it over. Same keys, same enums:

```csharp
var route = new SpaObject(
    SpaType.ObjectParamRoute,
    SpaParamType.Route,
    [
        new SpaPodProperty(SpaParamRoute.Index, SpaPodPropFlags.None, new SpaInt(3)),
        new SpaPodProperty(SpaParamRoute.Device, SpaPodPropFlags.None, new SpaInt(0)),
    ]);

Console.WriteLine(route);
```

The flags in the middle are `SpaPodPropFlags`, where `Mandatory` and `DontFixate` matter during
format negotiation and nothing else usually does.

## Raw bytes, when you need them

`SpaPod` converts between the value model and the wire format, which is what you want when something
hands you a parameter as bytes or expects one:

```csharp
byte[] bytes = SpaPod.ToBytes(new SpaInt(48000));
if (SpaPod.TryParse(bytes, out SpaValue? value) && value is SpaInt rate)
    Console.WriteLine(rate.Value);
```

`TryParse` rather than `Parse`: a pod from the wire is untrusted input, and a truncated or malformed
one is a `false` rather than an exception.

The builder and reader used inside the library are internal on purpose. If you need something they
can do that the value model cannot express, that is worth an issue rather than a workaround.

## Where the names come from

The parameter families (`SpaParamRoute`, `SpaParamProfile`, `SpaProp`, `SpaFormat`,
`SpaParamBuffers`, ...) are generated from SPA's own headers, so they carry upstream's spelling with
C# casing. When a value looks unfamiliar, `spa/param/` in the PipeWire sources is the reference, and
`pw-cli enum-params <id> <param>` prints what a real object answers so you can compare.

## Names that look alike

`SpaProp` is the generated enum of `SPA_PROP_*` ids - the *key* of a property inside a Props object.
`SpaPodProperty` is a property itself, a key/flags/value triple inside any object. Two letters apart,
unrelated jobs. [choosing-a-type.md](choosing-a-type.md) lists the other pairs worth not confusing.
