﻿/// A module for converting log lines to text/strings.
module Logary.Formatting

open System
open System.Globalization
open System.Text

open Microsoft.FSharp.Reflection

open Newtonsoft.Json

open Newtonsoft.Json.FSharp

open Logary.Measure

/// Returns the case name of the object with union type 'ty.
let internal caseNameOf (x:'a) =
  match FSharpValue.GetUnionFields(x, typeof<'a>) with
  | case, _ -> case.Name

/// Formats the data in a nice fashion for printing to e.g. the Debugger or Console.
let internal formatData (nl : string) (data : Map<string, obj>) =
  // depth = 0-based 1-counting index of depth
  let rec f (depth : int) (data : Map<string, obj>) =
    let app (s : string) (sb : StringBuilder) =
      sb.Append s
    let inspect = box >> function
      | null -> "null"
      | :? string as s -> "\"" + s + "\""
      | x when x.GetType().IsAssignableFrom(typeof<Map<string, obj>>) ->
        let data' = x :?> Map<string, obj>
        f (depth + 1) data' // recursively print maps
      | x -> x.ToString()
    data
    |> Map.fold (fun sb k t ->
        sb
        |> app nl
        |> app (new String(' ', depth * 2 + 2))
        |> app k
        |> app " => "
        |> app (inspect t))
      (StringBuilder())
    |> fun sb -> sb.ToString()
  f 0 data

/// A StringFormatter is the thing that takes a log line and returns it as a string
/// that can be printed, sent or otherwise dealt with in a manner that suits the target.
type StringFormatter =
  { format   : LogLine -> string
    m_format : Measure -> string }
  static member private Expanded nl ending =
    let format' =
      fun l ->
        let mex = l.``exception``
        sprintf "%s %s: %s [%s]%s%s%s%s"
          (string (caseNameOf l.level).[0])
          // https://noda-time.googlecode.com/hg/docs/api/html/M_NodaTime_OffsetDateTime_ToString.htm
          (l.timestamp.ToDateTimeOffset().ToString("o", CultureInfo.InvariantCulture))
          l.message
          l.path
          (match l.tags with [] -> "" | _ -> " {" + String.Join(", ", l.tags) + "}")
          (if l.data.IsEmpty then "" else formatData nl l.data)
          (match mex with None -> "" | Some ex -> sprintf " cont...%s%O" nl ex)
          ending
    { format   = format'
      m_format = LogLine.fromMeasure >> format' }

  /// Verbatim simply outputs the message and no other information
  /// and doesn't append a newline to the string.
  static member Verbatim =
    { format   = fun l -> l.message
      m_format = Measure.getValueStr }

  /// VerbatimNewline simply outputs the message and no other information
  /// and does append a newline to the string.
  static member VerbatimNewline =
    { format = fun l -> sprintf "%s%s" l.message (Environment.NewLine)
      m_format = fun m -> sprintf "%s%s" (Measure.getValueStr m) (Environment.NewLine) }

  /// <see cref="StringFormatter.LevelDatetimePathMessageNl" />
  static member LevelDatetimeMessagePath =
    StringFormatter.Expanded (Environment.NewLine) ""

  /// LevelDatetimePathMessageNl outputs the most information of the log line
  /// in text format, starting with the level as a single character,
  /// then the ISO8601 format of a DateTime (with +00:00 to show UTC time),
  /// then the path in square brackets: [Path.Here], the message and a newline.
  /// Exceptions are called ToString() on and prints each line of the stack trace
  /// newline separated.
  static member LevelDatetimeMessagePathNl =
    StringFormatter.Expanded (Environment.NewLine) (Environment.NewLine)

open NodaTime.TimeZones
open NodaTime.Serialization.JsonNet

/// A LogLevel to/from string converter for Json.Net.
type LogLevelStringConverter() =
  inherit JsonConverter()

  override x.CanConvert typ =
    typ = typeof<LogLevel>

  override x.WriteJson(writer, value, serializer) =
    match value :?> LogLevel with | _ as v -> writer.WriteRawValue(sprintf "\"%O\"" v)

  override x.ReadJson(reader, t, _, serializer) =
    match reader.TokenType with
    | JsonToken.Null   -> LogLevel.Info |> box
    | JsonToken.String -> LogLevel.FromString(reader.ReadAsString()) |> box
    | _ as t -> failwithf "invalid token %A when trying to read LogLevel" t

/// Wrapper that constructs a Json.Net JSON formatter.
type JsonFormatter =
  /// Construct some JSON.Net JsonSerializerSettings, with all the
  /// types that are relevant to serialize in a custom way in Logary.
  static member Settings ?funOpts =
    JsonSerializerSettings()
    |> fun o -> (defaultArg funOpts (fun x -> x)) o
    |> fun o -> o.Converters.Add <| LogLevelStringConverter() ; o
    |> Serialisation.extend
    |> fun o -> o.ConfigureForNodaTime(new DateTimeZoneCache(TzdbDateTimeZoneSource.Default))

  /// Create a new JSON formatter with optional function that can modify the serialiser
  /// settings before any other alteration is done.
  static member Default ?funOpts =
    let opts =
      match funOpts with
      | None -> JsonFormatter.Settings()
      | Some f -> JsonFormatter.Settings(f)
    let serialiser = JsonSerializer.Create opts
    { format =
        fun ll ->
          use sw = new System.IO.StringWriter()
          use w = new JsonTextWriter(sw)
          serialiser.Serialize(w, ll)
          sw.ToString()
      m_format =
        fun m ->
          use sw = new System.IO.StringWriter()
          use w = new JsonTextWriter(sw)
          serialiser.Serialize(w, m)
          sw.ToString() }
