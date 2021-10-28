using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AdSync
{
    public class OrderedStringListJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(List<string>);
        }

        public override object ReadJson(
            JsonReader reader, Type objectType,
            object existingValue, JsonSerializer serializer)
        {
            var list = serializer.Deserialize<string>(reader) ?? string.Empty;
            return list.Split('\r').ToList<string>();
        }

        public override void WriteJson(JsonWriter writer, object value,
                                       JsonSerializer serializer)
        {
            var ordered = string.Join("\r", ((List<string>)value).OrderBy(i => i, StringComparer.OrdinalIgnoreCase));
            serializer.Serialize(writer, ordered);
        }
    }
}
