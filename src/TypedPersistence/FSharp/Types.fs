namespace TypedPersistence.FSharp

open FSharp.Reflection
open LiteDB
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.Linq.Expressions
open System.Text.RegularExpressions

[<AutoOpen>]
module Types =
    type GenericEntry<'a> =
        { id: string
          entry: 'a }

    // This stuff is a patched version of stuff from LiteDB.FSharp for LiteDB 5, I also provided as a PR
    // I hope I can remove this once LiteDB.FSharp works with LiteDB 5
    [<AutoOpen>]
    module ReflectionAdapters =
        open System.Reflection

        type System.Type with
            member this.IsValueType = this.GetTypeInfo().IsValueType
            member this.IsGenericType = this.GetTypeInfo().IsGenericType
            member this.GetMethod(name) = this.GetTypeInfo().GetMethod(name)
            member this.GetGenericArguments() = this.GetTypeInfo().GetGenericArguments()
            member this.MakeGenericType(args) = this.GetTypeInfo().MakeGenericType(args)
            member this.GetCustomAttributes(inherits : bool) : obj[] =
                downcast box(CustomAttributeExtensions.GetCustomAttributes(this.GetTypeInfo(), inherits) |> Seq.toArray)

    type Kind =
        | Other = 0
        | Option = 1
        | Tuple = 2
        | Union = 3
        | DateTime = 6
        | MapOrDictWithNonStringKey = 7
        | Long = 8
        | BigInt = 9
        | Guid = 10
        | Decimal = 11
        | Binary = 12
        | ObjectId = 13
        | Double = 14
        | Record = 15

    /// Helper for serializing map/dict with non-primitive, non-string keys such as unions and records.
    /// Performs additional serialization/deserialization of the key object and uses the resulting JSON
    /// representation of the key object as the string key in the serialized map/dict.
    type MapSerializer<'k,'v when 'k : comparison>() =
        static member Deserialize(t:Type, reader:JsonReader, serializer:JsonSerializer) =
            let dictionary =
                serializer.Deserialize<Dictionary<string,'v>>(reader)
                    |> Seq.fold (fun (dict:Dictionary<'k,'v>) kvp ->
                        use tempReader = new System.IO.StringReader(kvp.Key)
                        let key = serializer.Deserialize(tempReader, typeof<'k>) :?> 'k
                        dict.Add(key, kvp.Value)
                        dict
                        ) (Dictionary<'k,'v>())
            if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<_,_>>
            then dictionary |> Seq.map (|KeyValue|) |> Map.ofSeq :> obj
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Dictionary<_,_>>
            then dictionary :> obj
            else failwith "MapSerializer input type wasn't a Map or a Dictionary"
        static member Serialize(value: obj, writer:JsonWriter, serializer:JsonSerializer) =
            let kvpSeq =
                match value with
                | :? Map<'k,'v> as mapObj -> mapObj |> Map.toSeq
                | :? Dictionary<'k,'v> as dictObj -> dictObj |> Seq.map (|KeyValue|)
                | _ -> failwith "MapSerializer input value wasn't a Map or a Dictionary"
            writer.WriteStartObject()
            use tempWriter = new System.IO.StringWriter()
            kvpSeq
                |> Seq.iter (fun (k,v) ->
                    let key =
                        tempWriter.GetStringBuilder().Clear() |> ignore
                        serializer.Serialize(tempWriter, k)
                        tempWriter.ToString()
                    writer.WritePropertyName(key)
                    serializer.Serialize(writer, v) )
            writer.WriteEndObject()

    module private Cache =

        let jsonConverterTypes = ConcurrentDictionary<Type,Kind>()
        let serializationBinderTypes = ConcurrentDictionary<string,Type>()
        let inheritedConverterTypes = ConcurrentDictionary<string,HashSet<Type>>()
        let inheritedTypeQuickAccessor = ConcurrentDictionary<string * list<string>,Type>()

    open Cache

    [<RequireQualifiedAccess>]
    module DefaultValue =
        type DefaultGen<'t>() =
            member this.GetDefault() =
                let typeSignature = typeof<'t>.FullName
                if typeSignature = typeof<int>.FullName
                then unbox<'t> 0
                elif typeSignature = typeof<string>.FullName
                then unbox<'t> ""
                elif typeSignature = typeof<int64>.FullName
                then unbox<'t> 0L
                elif typeSignature = typeof<bigint>.FullName
                then unbox<'t> 0I
                elif typeSignature = typeof<bool>.FullName
                then unbox<'t> false
                elif typeSignature = typeof<Guid>.FullName
                then unbox<'t> Guid.Empty
                elif typeSignature = typeof<DateTime>.FullName
                then unbox<'t> (DateTime(1970, 1, 1, 0, 0, 0))
                elif typeof<'t>.Name = "FSharpOption`1"
                then unbox Option<'t>.None
                elif typeSignature = typeof<float>.FullName
                then unbox 0.0
                else
                Unchecked.defaultof<'t>

        let fromType (inputType: System.Type) : obj =
            let genericDefaultGenType = typedefof<DefaultGen<_>>.MakeGenericType(inputType)
            let defaultGenerator = Activator.CreateInstance(genericDefaultGenType)
            let getDefaultMethod = genericDefaultGenType.GetMethods() |> Seq.filter (fun meth -> meth.Name = "GetDefault") |> Seq.head
            getDefaultMethod.Invoke(defaultGenerator, [||])

    /// Converts F# options, tuples and unions to a format understandable
    /// A derivative of Fable's JsonConverter. Code adapted from Lev Gorodinski's original.
    /// See https://goo.gl/F6YiQk
    type FSharpJsonConverter() =
        inherit Newtonsoft.Json.JsonConverter()
        let advance(reader: JsonReader) =
            reader.Read() |> ignore

        let readElements(reader: JsonReader, itemTypes: Type[], serializer: JsonSerializer) =
            let rec read index acc =
                match reader.TokenType with
                | JsonToken.EndArray -> acc
                | _ ->
                    let value = serializer.Deserialize(reader, itemTypes.[index])
                    advance reader
                    read (index + 1) (acc @ [value])
            advance reader
            read 0 List.empty

        let getUci t name =
            FSharpType.GetUnionCases(t)
            |> Array.find (fun uci -> uci.Name = name)

        let isRegisteredParentType (tp: Type) =
            inheritedConverterTypes.ContainsKey(tp.FullName)

        override x.CanConvert(t) =
            let kind =
                jsonConverterTypes.GetOrAdd(t, fun t ->
                    if t.FullName = "System.DateTime"
                    then Kind.DateTime
                    elif t.FullName = "System.Guid"
                    then Kind.Guid
                    elif t.Name = "FSharpOption`1"
                    then Kind.Option
                    elif t.FullName = "System.Int64"
                    then Kind.Long
                    elif t.FullName = "System.Double"
                    then Kind.Double
                    elif t = typeof<LiteDB.ObjectId>
                    then Kind.ObjectId
                    elif t.FullName = "System.Numerics.BigInteger"
                    then Kind.BigInt
                    elif t = typeof<byte[]>
                    then Kind.Binary
                    elif FSharpType.IsTuple t
                    then Kind.Tuple
                    elif (FSharpType.IsUnion t && t.Name <> "FSharpList`1")
                    then Kind.Union
                    elif (FSharpType.IsRecord t)
                    then Kind.Record
                    elif t.IsGenericType
                        && (t.GetGenericTypeDefinition() = typedefof<Map<_,_>> || t.GetGenericTypeDefinition() = typedefof<Dictionary<_,_>>)
                        && t.GetGenericArguments().[0] <> typeof<string>
                    then
                        Kind.MapOrDictWithNonStringKey
                    else Kind.Other)

            match kind with
            | Kind.Other -> isRegisteredParentType t
            | _ -> true

        override x.WriteJson(writer, value, serializer) =
            if isNull value
            then serializer.Serialize(writer, value)
            else
                let t = value.GetType()
                match jsonConverterTypes.TryGetValue(t) with
                | false, _ ->
                    serializer.Serialize(writer, value)
                | true, Kind.Long ->
                    let numberLong = JObject()
                    let value = sprintf "%+i" (value :?> int64)
                    numberLong.Add(JProperty("$numberLong", value))
                    numberLong.WriteTo(writer)
                | true, Kind.Double ->
                    let value = (value :?> double).ToString("R")
                    writer.WriteValue(value)
                | true, Kind.Guid ->
                    let guidObject = JObject()
                    let guidValue = (value :?> Guid).ToString()
                    guidObject.Add(JProperty("$guid", guidValue))
                    guidObject.WriteTo(writer)
                | true, Kind.ObjectId ->
                    let objectId = value |> unbox<ObjectId>
                    let oid = JObject()
                    oid.Add(JProperty("$oid", objectId.ToString()))
                    oid.WriteTo(writer)
                | true, Kind.DateTime ->
                    let dt = value :?> DateTime
                    let universalTime = if dt.Kind = DateTimeKind.Local then dt.ToUniversalTime() else dt
                    let dateTime = JObject()
                    dateTime.Add(JProperty("$date", universalTime.ToString("O", CultureInfo.InvariantCulture)))
                    dateTime.WriteTo(writer)
                | true, Kind.Binary ->
                    let bytes = value |> unbox<byte[]>
                    let base64 = Convert.ToBase64String(bytes)
                    let binaryBsonField = JObject()
                    binaryBsonField.Add(JProperty("$binary", base64))
                    binaryBsonField.WriteTo(writer)
                | true, Kind.Decimal ->
                    let value = (value :?> decimal).ToString()
                    let numberDecimal = JObject()
                    numberDecimal.Add(JProperty("$numberDecimal", value))
                    numberDecimal.WriteTo(writer)
                | true, Kind.Option ->
                    let _,fields = FSharpValue.GetUnionFields(value, t)
                    serializer.Serialize(writer, fields.[0])
                | true, Kind.Tuple ->
                    let values = FSharpValue.GetTupleFields(value)
                    serializer.Serialize(writer, values)
                | true, Kind.Union ->
                    let uci, fields = FSharpValue.GetUnionFields(value, t)
                    if fields.Length = 0
                    then serializer.Serialize(writer, uci.Name)
                    else
                        writer.WriteStartObject()
                        writer.WritePropertyName(uci.Name)
                        if fields.Length = 1
                        then serializer.Serialize(writer, fields.[0])
                        else serializer.Serialize(writer, fields)
                        writer.WriteEndObject()
                | true, Kind.MapOrDictWithNonStringKey ->
                    let mapTypes = t.GetGenericArguments()
                    let mapSerializer = typedefof<MapSerializer<_,_>>.MakeGenericType mapTypes
                    let mapSerializeMethod = mapSerializer.GetMethod("Serialize")
                    mapSerializeMethod.Invoke(null, [| value; writer; serializer |]) |> ignore
                | true, Kind.Record ->
                    let fields = FSharpType.GetRecordFields(t)
                    writer.WriteStartObject()
                    for fieldType in fields do
                        let fieldValue = FSharpValue.GetRecordField(value, fieldType)
                        writer.WritePropertyName(fieldType.Name)
                        serializer.Serialize(writer, fieldValue)
                    writer.WriteEndObject()

                | true, _ ->
                    serializer.Serialize(writer, value)

        override x.ReadJson(reader, t, existingValue, serializer) =
            match jsonConverterTypes.TryGetValue(t) with
            | false, _ ->
                serializer.Deserialize(reader, t)
            | true, Kind.Guid ->
                let jsonObject = JObject.Load(reader)
                let value = jsonObject.["$guid"].Value<string>()
                upcast Guid.Parse(value)
            | true, Kind.ObjectId ->
                let jsonObject = JObject.Load(reader)
                let value = jsonObject.["$oid"].Value<string>()
                upcast ObjectId(value)
            | true, Kind.Decimal ->
                let jsonObject = JObject.Load(reader)
                let value = jsonObject.["$numberDecimal"].Value<string>()
                upcast Decimal.Parse(value)
            | true, Kind.Binary ->
                let jsonObject =  JObject.Load(reader)
                let base64 = jsonObject.["$binary"].Value<string>()
                let bytes = Convert.FromBase64String(base64)
                upcast bytes
            | true, Kind.Long ->
                let jsonObject = JObject.Load(reader)
                let value = jsonObject.["$numberLong"].Value<string>()
                upcast Int64.Parse(value)
            | true, Kind.Double ->
                let value = serializer.Deserialize(reader, typeof<string>) :?> string
                upcast Double.Parse(value)
            | true, Kind.DateTime ->
                let jsonObject = JObject.Load(reader)
                let dateValue = jsonObject.["$date"].Value<string>()
                let date = DateTime.Parse(dateValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                upcast (if date.Kind = DateTimeKind.Local then date.ToUniversalTime() else date)
            | true, Kind.Option ->
                let innerType = t.GetGenericArguments().[0]
                let innerType =
                    if innerType.IsValueType
                    then (typedefof<Nullable<_>>).MakeGenericType([|innerType|])
                    else innerType

                let cases = FSharpType.GetUnionCases(t)

                let value =
                    match reader.TokenType with
                    | JsonToken.StartObject ->
                        let jObject = JObject.Load(reader)
                        let path = jObject.First.Path
                        if path.StartsWith("$") then
                            let value = jObject.GetValue(path)
                            value.ToObject(innerType,serializer)
                        else
                            jObject.ToObject(innerType,serializer)
                    | JsonToken.Null -> null
                    | _ -> serializer.Deserialize(reader,innerType)

                if isNull value
                then FSharpValue.MakeUnion(cases.[0], [||])
                else FSharpValue.MakeUnion(cases.[1], [|value|])
            | true, Kind.Tuple ->
                match reader.TokenType with
                | JsonToken.StartArray ->
                    let values = readElements(reader, FSharpType.GetTupleElements(t), serializer)
                    FSharpValue.MakeTuple(values |> List.toArray, t)
                | _ -> failwith "invalid token"
            | true, Kind.Union ->
                match reader.TokenType with
                | JsonToken.String ->
                    let name = serializer.Deserialize(reader, typeof<string>) :?> string
                    FSharpValue.MakeUnion(getUci t name, [||])
                | JsonToken.StartObject ->
                    advance reader
                    let name = reader.Value :?> string
                    let uci = getUci t name
                    advance reader
                    let itemTypes = uci.GetFields() |> Array.map (fun pi -> pi.PropertyType)
                    if itemTypes.Length > 1
                    then
                        let values = readElements(reader, itemTypes, serializer)
                        advance reader
                        FSharpValue.MakeUnion(uci, List.toArray values)
                    else
                        let value = serializer.Deserialize(reader, itemTypes.[0])
                        advance reader
                        FSharpValue.MakeUnion(uci, [|value|])
                | JsonToken.Null -> null // for { "union": null }
                | _ -> failwith "invalid token"
            | true, Kind.MapOrDictWithNonStringKey ->
                let mapTypes = t.GetGenericArguments()
                let mapSerializer = typedefof<MapSerializer<_,_>>.MakeGenericType mapTypes
                let mapDeserializeMethod = mapSerializer.GetMethod("Deserialize")
                mapDeserializeMethod.Invoke(null, [| t; reader; serializer |])
            | true, Kind.Other when isRegisteredParentType t ->
                let inheritedTypes = inheritedConverterTypes.[t.FullName]
                let jObject = JObject.Load(reader)
                let jsonFields = jObject.Properties() |> Seq.map (fun prop -> prop.Name) |> List.ofSeq
                let inheritedType = inheritedTypeQuickAccessor.GetOrAdd((t.FullName,jsonFields),fun (_,jsonFields) ->
                    let findType (jsonFields: seq<string>) =
                        inheritedTypes |> Seq.maxBy (fun tp ->
                            let fields =
                                let properties = tp.GetProperties() |> Array.filter(fun prop -> prop.CanWrite) |> Array.map (fun prop -> prop.Name)
                                if properties.Length > 0 then properties
                                else
                                    tp.GetFields() |> Array.map (fun fd -> fd.Name)
                            let fieldsLength = Seq.length fields
                            (jsonFields |> Seq.filter(fun jsonField ->
                                Seq.contains jsonField fields
                            )
                            |> Seq.length),-fieldsLength
                        )
                    findType jsonFields
                )
                // printfn "found inherited type %s with jsonFields %A" inheritedType.FullName jsonFields
                jObject.ToObject(inheritedType,serializer)

            | true, Kind.Record ->
                let recordJson = JObject.Load(reader)
                let recordFields = FSharpType.GetRecordFields(t)
                let recordValues = Array.init recordFields.Length <| fun index ->
                    let recordField = recordFields.[index]
                    let fieldType = recordField.PropertyType
                    let fieldName = recordField.Name
                    match recordJson.TryGetValue fieldName with
                    | true, fieldValueJson -> fieldValueJson.ToObject(fieldType, serializer)
                    | false, _ -> DefaultValue.fromType fieldType

                FSharpValue.MakeRecord(t, recordValues)

            | true, _ ->
                serializer.Deserialize(reader, t)


    /// Utilities to convert between BSON document and F# types
    [<RequireQualifiedAccess>]
    module Bson =
        /// Returns the value of entry in the BsonDocument by it's key
        let read (key: string) (doc: BsonDocument) =
            doc.[key]

        /// Reads a property from a BsonDocument by it's key as a string
        let readStr (key: string) (doc: BsonDocument) =
            doc.[key].AsString

        /// Reads a property from a BsonDocument by it's key and converts it to an integer
        let readInt (key: string) (doc: BsonDocument) =
            doc.[key].AsString |> int

        /// Reads a property from a BsonDocument by it's key and converts it to an integer
        let readBool (key: string) (doc: BsonDocument) =
            doc.[key].AsString |> bool.Parse

        /// Adds an entry to a `BsonDocument` given a key and a BsonValue
        let withKeyValue key value (doc: BsonDocument) =
            doc.Add(key, value)
            doc

        /// Reads a field from a BsonDocument as DateTime
        let readDate (key: string) (doc: BsonDocument) =
            let date = doc.[key].AsDateTime
            if date.Kind = DateTimeKind.Local
            then date.ToUniversalTime()
            else date

        /// Removes an entry (property) from a `BsonDocument` by the key of that property
        let removeEntryByKey (key:string) (doc: BsonDocument) =
            if (doc.ContainsKey key)
            then doc.Remove(key) |> ignore
            doc

        let private fsharpJsonConverter = FSharpJsonConverter()
        let mutable internal converters : JsonConverter[] = [| fsharpJsonConverter |]

        /// Converts a typed entity (normally an F# record) to a BsonDocument.
        /// Assuming there exists a field called `Id` or `id` of the record that will be mapped to `_id` in the BsonDocument, otherwise an exception is thrown.
        let serialize<'t> (entity: 't) =
            let typeName = typeof<'t>.Name
            let json = JsonConvert.SerializeObject(entity, converters)
            let doc = LiteDB.JsonSerializer.Deserialize(json) |> unbox<LiteDB.BsonDocument>
            for key in doc.Keys do
                if key.EndsWith("@")
                then doc.Remove(key) |> ignore

            doc.Keys
            |> Seq.tryFind (fun key -> key = "Id" || key = "id" || key = "_id")
            |> function
                | Some key ->
                   doc
                   |> withKeyValue "_id" (read key doc)
                   |> removeEntryByKey key
                | None ->
                  let error = sprintf "Expected type %s to have a unique identifier property of 'Id' or 'id' (exact name)" typeName
                  failwith error

        /// Converts a BsonDocument to a typed entity given the document the type of the CLR entity.
        let deserializeByType (entity: BsonDocument) (entityType: Type) =
            let getCollectionElementType (collectionType:Type) =
                let typeNames = ["FSharpList`1";"IEnumerable`1";"List"; "List"; "IList"; "FSharpOption"]
                let typeName = collectionType.Name
                if List.contains typeName typeNames then
                    collectionType.GetGenericArguments().[0]
                else if collectionType.IsArray then
                    collectionType.GetElementType()
                else failwithf "Could not extract element type from collection of type %s"  collectionType.FullName

            let getKeyFieldName (entityType: Type) =
                if FSharpType.IsRecord entityType
                then FSharpType.GetRecordFields entityType
                    |> Seq.tryFind (fun field -> field.Name = "Id" || field.Name = "id")
                    |> function
                        | Some field -> field.Name
                        | None -> "Id"
                else "Id"

            let rewriteIdentityKeys (entity:BsonDocument) =
                let rec rewriteKey (keys:string list) (entity:BsonDocument) (entityType: Type) key =
                    match keys with
                    | []  -> ()
                    | y :: ys ->
                        let continueToNext() = rewriteKey ys entity entityType key
                        match y, entity.[y] with
                        // during deserialization, turn key-prop _id back into original Id or id
                        | "_id", id ->
                            entity
                            |> withKeyValue key id
                            |> removeEntryByKey "_id"
                            |> (ignore >> continueToNext)

                        | "$id", id ->
                            entity
                            |> withKeyValue key id
                            |> removeEntryByKey "$id"
                            |> (ignore >> continueToNext)

                        |_, (:? BsonDocument as bson) ->
                            // if property is nested record that resulted from DbRef then
                            // also re-write the transformed _id key property back to original Id or id
                            let propType = entityType.GetProperty(y).PropertyType
                            if FSharpType.IsRecord propType
                            then rewriteKey (List.ofSeq bson.Keys) bson propType (getKeyFieldName propType)
                            continueToNext()

                        |_, (:? BsonArray as bsonArray) ->
                            // if property is BsonArray then loop through each element
                            // and if that element is a record, then re-write _id back to original
                            let collectionType = entityType.GetProperty(y).PropertyType
                            let elementType = getCollectionElementType collectionType
                            if FSharpType.IsRecord elementType then
                                let docKey = getKeyFieldName elementType
                                for bson in bsonArray do
                                    if bson.IsDocument
                                    then
                                      let doc = bson.AsDocument
                                      let keys = List.ofSeq doc.Keys
                                      rewriteKey keys doc elementType docKey

                            continueToNext()
                        |_ ->
                            continueToNext()

                let keys = List.ofSeq entity.Keys
                rewriteKey keys entity entityType (getKeyFieldName entityType)
                entity

            rewriteIdentityKeys entity
            |> LiteDB.JsonSerializer.Serialize
            |> fun json -> JsonConvert.DeserializeObject(json, entityType, converters)

        let serializeField(any: obj) : BsonValue =
            // Entity => Json => Bson
            let json = JsonConvert.SerializeObject(any, Formatting.None, converters);
            LiteDB.JsonSerializer.Deserialize(json);

        /// Deserializes a field of a BsonDocument to a typed entity
        let deserializeField<'t> (value: BsonValue) =
            // Bson => Json => Entity<'t>
            let typeInfo = typeof<'t>
            value
            // Bson to Json
            |> LiteDB.JsonSerializer.Serialize
            // Json to 't
            |> fun json -> JsonConvert.DeserializeObject(json, typeInfo, converters)
            |> unbox<'t>

        /// Converts a BsonDocument to a typed entity given the document the type of the CLR entity.
        let deserialize (t: Type) (entity: BsonDocument) =
            // if the type is already a BsonDocument, then do not deserialize, just return as is.
            if t.FullName = typeof<BsonDocument>.FullName
            then
                entity :> obj
            else
                deserializeByType entity t

    type FSharpRecordEntityMapper(forType) as this =
        inherit EntityMapper(forType)

        do
            this.CreateInstance <-
                new CreateObject(Bson.deserialize forType)

    type FSharpBsonMapperWithGenerics() as this =
        inherit BsonMapper()

        let resolveCollectionName = this.ResolveCollectionName

        let removeInvalidChars (name: string) =
            Regex.Replace(name, "[`0-9]", "")

        let rec genericName (t: Type) =
            if t.IsGenericType then
                t.GetGenericArguments()
                |> List.ofArray
                |> List.map genericName
                |> List.reduce (fun a b -> a + "$" + b)
                |> (+) ((t.Name |> removeInvalidChars) + "_")
            else
                resolveCollectionName.Invoke(t)
                |> removeInvalidChars

        let resolveCollectionName (t: Type) =
            if t.IsGenericType then
                genericName t
            else
                resolveCollectionName.Invoke(t)
                |> removeInvalidChars

        do
            this.ResolveCollectionName <-
                Func<Type, string>(fun t ->
                    let result =
                        match this.FallbackFor with
                        | Some t2 ->
                            resolveCollectionName t2
                        | None ->
                            resolveCollectionName t
                    result)

        let entityMappers = Dictionary<Type,EntityMapper>()

        member val FallbackFor: Type option = None with get, set

        member this.DbRef<'T1,'T2> (exp: Expression<Func<'T1,'T2>>) =
            this.Entity<'T1>().DbRef(exp) |> ignore

        static member RegisterInheritedConverterType<'T1,'T2>() =
            let t1 = typeof<'T1>
            let t2 = typeof<'T2>
            Cache.inheritedConverterTypes.AddOrUpdate(
                t1.FullName,
                HashSet [t2],
                ( fun _ types -> types.Add(t2) |> ignore; types )
            ) |> ignore

        static member UseCustomJsonConverters(converters: JsonConverter[]) =
            Bson.converters <- converters

        override self.ToDocument<'t>(entity: 't) =
            //Add DBRef Feature :set field value with $ref
            if typeof<'t>.FullName = typeof<BsonDocument>.FullName
            then entity |> unbox<BsonDocument>
            else
            let withEntityMap (doc:BsonDocument)=
                let mapper = entityMappers.Item (entity.GetType())
                for memberMapper in mapper.Members do
                    if not (isNull memberMapper.Serialize) then
                        let value = memberMapper.Getter.Invoke(entity)
                        let serialized = memberMapper.Serialize.Invoke(value, self)
                        doc.[memberMapper.FieldName] <- serialized
                doc
            Bson.serialize<'t> entity
            |> withEntityMap

        override __.BuildEntityMapper(entityType) =
            let mapper =
                if FSharpType.IsRecord entityType then
                    new FSharpRecordEntityMapper(entityType) :> EntityMapper
                else
                    base.BuildEntityMapper(entityType)
            entityMappers.Add(entityType, mapper)
            mapper

    type SavingResult = Inserted | Updated

    type LoadError =
        | DatabaseNotExisting
        | DocumentNotExisting
