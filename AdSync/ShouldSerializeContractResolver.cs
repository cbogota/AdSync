using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AdSync
{
    public class ShouldSerializeContractResolver : DefaultContractResolver
    {
        public static readonly ShouldSerializeContractResolver Instance = new ShouldSerializeContractResolver();

        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);
            if (property.DeclaringType != typeof(Entry)) return property;
            var fieldInfo = typeof(Entry).GetProperty(property.PropertyName);
            if (fieldInfo?.PropertyType.GetInterfaces().Contains(typeof (IEnumerable)) ?? false)
                property.ShouldSerialize = instance => ((IEnumerable)fieldInfo.GetValue(instance))?.GetEnumerator().MoveNext() ?? false;
            if (fieldInfo?.PropertyType == typeof(bool))
                property.ShouldSerialize = instance => (bool)fieldInfo.GetValue(instance);
            return property;
        }
    }
}
