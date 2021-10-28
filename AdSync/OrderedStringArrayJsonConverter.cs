using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AdSync
{
    public class OrderedStringArrayJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(string[]);
        }

        public override object ReadJson(
            JsonReader reader, Type objectType,
            object existingValue, JsonSerializer serializer)
        {
            var list = serializer.Deserialize<string>(reader) ?? string.Empty;
            return list.Split('\r');
        }

        public override void WriteJson(JsonWriter writer, object value,
                                       JsonSerializer serializer)
        {
            var ordered = string.Join("\r", ((string[])value).OrderBy(i => i, StringComparer.OrdinalIgnoreCase));
            serializer.Serialize(writer, ordered);
        }
    }
}
