﻿module FSharp.Monad.Tests.ListTest

open System
open FSharp.Monad.Iteratee
open FSharp.Monad.Iteratee.List
open NUnit.Framework
open FsUnit

[<Test>]
let ``test List_split correctly breaks the list on the specified predicate``() =
  let str = List.ofSeq "Howdy! Want to play?"
  let expected = (List.ofSeq "Howdy!", List.ofSeq " Want to play?")
  List.split (fun c -> c = ' ') str |> should equal expected

[<Test>]
let ``test List_splitAt correctly breaks the list on the specified index``() =
  let str = List.ofSeq "Howdy! Want to play?"
  let expected = (List.ofSeq "Howdy!", List.ofSeq " Want to play?")
  List.splitAt 6 str |> should equal expected

let runTest i =
  match run i with
  | Choice1Of2 e -> raise e
  | Choice2Of2 x -> x

[<Test>]
let ``test length should calculate the length of the list without modification``() =
  let actual = enumerate [1;2;3] length |> runTest 
  actual |> should equal 3

let testPeekAndHead = [|
  [| box ([]:char list); box None |]
  [| box ['c']; box (Some 'c') |]
  [| box ['c';'h';'a';'r']; box (Some 'c') |]
|]

[<Test>]
[<TestCaseSource("testPeekAndHead")>]
let ``test peek should return the value without removing it from the stream``(input:char list, expected:char option) =
  let actual = enumerate input peek |> runTest 
  actual |> should equal expected

[<Test>]
[<TestCaseSource("testPeekAndHead")>]
let ``test head should return the value and remove it from the stream``(input:char list, expected:char option) =
  let actual = enumerate input head |> runTest
  actual |> should equal expected

[<Test>]
[<Sequential>]
let ``test drop should drop the first n items``([<Values(0,1,2,3,4,5,6,7,8,9)>] x) =
  let drop2Head = iteratee {
    do! drop x
    return! head }
  let actual = enumerate [0..9] drop2Head |> runTest
  actual |> should equal (Some x)

[<Test>]
let ``test split should correctly split the input``() =
  let actual = enumeratePure1Chunk (List.ofSeq "abcde") (split ((=) 'c')) |> runTest
  actual |> should equal ['a';'b']

[<Test>]
let ``test heads should count the number of characters in a set of headers``() =
  let actual = enumeratePure1Chunk (List.ofSeq "abd") (heads (List.ofSeq "abc")) |> runTest
  actual |> should equal 2

let readLinesTests = [|
  [| box ""; box (Choice2Of2 []:Choice<String list, String list>) |]
  [| box "line1"; box (Choice1Of2 []:Choice<String list, String list>) |]
  [| box "line1\n"; box (Choice2Of2 ["line1"]:Choice<String list, String list>) |]
  [| box "line1\r"; box (Choice2Of2 ["line1"]:Choice<String list, String list>) |]
  [| box "line1\r\n"; box (Choice2Of2 ["line1"]:Choice<String list, String list>) |]
  [| box "line1\r\nline2"; box (Choice1Of2 ["line1"]:Choice<String list, String list>) |]
  [| box "line1\r\nline2\r\n"; box (Choice2Of2 ["line1";"line2"]:Choice<String list, String list>) |]
  [| box "line1\r\nline2\r\nline3\r\nline4\r\nline5"; box (Choice1Of2 ["line1";"line2";"line3";"line4"]:Choice<String list, String list>) |]
  [| box "line1\r\nline2\r\nline3\r\nline4\r\nline5\r\n"
     box (Choice2Of2 ["line1";"line2";"line3";"line4";"line5"]:Choice<String list, String list>) |]
  [| box "PUT /file HTTP/1.1\r\nHost: example.com\rUser-Agent: X\nContent-Type: text/plain\r\n\r\n1C\r\nbody line 2\r\n\r\n7"
     box (Choice2Of2 ["PUT /file HTTP/1.1";"Host: example.com";"User-Agent: X";"Content-Type: text/plain"]:Choice<String list, String list>) |]
|]
[<Test>]
[<TestCaseSource("readLinesTests")>]
let ``test readLines should return the lines from the input``(input, expected:Choice<String list, String list>) =
  let actual = enumeratePure1Chunk (List.ofSeq input) readLines |> runTest
  actual |> should equal expected

[<Ignore("Get enumeratePureNChunk to correctly parse \r and \n as newline markers on their own.")>]
[<Test>]
[<TestCaseSource("readLinesTests")>]
let ``test readLines should return the lines from the input when chunked``(input, expected:Choice<String list, String list>) =
  let actual = enumeratePureNChunk (List.ofSeq input) 5 readLines |> runTest
  actual |> should equal expected