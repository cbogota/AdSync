using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AdSync
{
    public class OrderedIntHashSetJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(HashSet<int>);
        }

        public override object ReadJson(
            JsonReader reader, Type objectType,
            object existingValue, JsonSerializer serializer)
        {
            var list = serializer.Deserialize<string>(reader) ?? string.Empty;
            return new HashSet<int>(list.Split(',').Where(t => int.TryParse(t, out var i)).Select(t => int.Parse(t)));
        }

        public override void WriteJson(JsonWriter writer, object value,
                                       JsonSerializer serializer)
        {
            if (value == null)
                serializer.Serialize(writer, null);
            var ordered = string.Join(",", ((HashSet<int>)value).OrderBy(i => i));
            serializer.Serialize(writer, ordered);
        }
    }
}
