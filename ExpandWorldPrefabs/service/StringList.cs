using System;
using System.Collections.Generic;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Service;

/// <summary>
/// YAML value that accepts either one scalar string or a sequence of strings.
/// This keeps the short form convenient without reserving separator characters
/// inside user-provided text.
/// </summary>
public sealed class StringList : List<string>
{
}

internal sealed class StringListYamlConverter : IYamlTypeConverter
{
  public bool Accepts(Type type) => type == typeof(StringList);

  public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
  {
    if (parser.TryConsume<Scalar>(out var scalar))
    {
      var value = scalar.Value ?? "";
      if (scalar.Style == ScalarStyle.Plain &&
          (value.Length == 0 || value == "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase)))
        return new StringList();
      return new StringList { value };
    }

    parser.Consume<SequenceStart>();
    var values = new StringList();
    while (!parser.TryConsume<SequenceEnd>(out _))
      values.Add(parser.Consume<Scalar>().Value ?? "");
    return values;
  }

  public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
  {
    var values = (StringList?)value ?? [];
    if (values.Count == 1)
    {
      emitter.Emit(new Scalar(values[0]));
      return;
    }

    emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
    foreach (var item in values)
      emitter.Emit(new Scalar(item));
    emitter.Emit(new SequenceEnd());
  }
}
