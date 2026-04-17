using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitFamilyBuilder.Schema
{
    /// <summary>
    /// Backward-compatibility converter: deserializes a JSON value that may be
    /// either a single object <c>{...}</c> or an array <c>[{...}]</c> into a
    /// <see cref="List{T}"/>. A lone object is auto-wrapped as a one-element list.
    ///
    /// Used on <see cref="FamilyDefinition.Geometry"/> so older JSON payloads
    /// that sent <c>"geometry": { ... }</c> still parse into a single-element
    /// list without forcing callers to migrate in lockstep.
    /// </summary>
    public class SingleOrArrayListConverter<T> : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(List<T>);
        }

        public override object ReadJson(JsonReader reader, Type objectType,
            object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return new List<T>();

            JToken token = JToken.Load(reader);

            if (token.Type == JTokenType.Array)
                return token.ToObject<List<T>>(serializer);

            // Single object → auto-wrap into a one-element list.
            var list = new List<T> { token.ToObject<T>(serializer) };
            return list;
        }

        public override bool CanWrite { get { return false; } }

        public override void WriteJson(JsonWriter writer, object value,
            JsonSerializer serializer)
        {
            // Not used — CanWrite is false, so the default serializer emits arrays.
            throw new NotImplementedException();
        }
    }
}
