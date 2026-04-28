module Tests

open System.IO
open System.Text
open Expecto
open Giraffe
open Giraffe.ViewEngine
open Microsoft.AspNetCore.Http
open System.Threading

let private readBody (ctx: HttpContext) =
  ctx.Response.Body.Position <- 0L
  use reader = new StreamReader(ctx.Response.Body, Encoding.UTF8)
  reader.ReadToEnd()

let private makeCtx () =
  let ctx = DefaultHttpContext()
  ctx.Response.Body <- new MemoryStream()
  ctx

let private next: HttpFunc =
  Some
  >> Tasks.Task.FromResult

[<Tests>]
let classNameTests =
  testList "CSS class names" [
    test "deterministic for the same input" {
      let a = Style.css "color: red;"
      let b = Style.css "color: red;"
      Expect.equal a.Name b.Name "Same CSS body should produce the same class name"
    }

    test "different for different inputs" {
      let a = Style.css "color: red;"
      let b = Style.css "color: blue;"
      Expect.notEqual a.Name b.Name "Different CSS bodies should produce different class names"
    }

    test "starts with c followed by 12 hex characters" {
      let c = Style.css "font-size: 16px;"
      Expect.isTrue (c.Name.StartsWith "c") "Class name should start with 'c'"
      Expect.equal c.Name.Length 13 "Class name should be 'c' + 12 hex chars"

      let hexPart = c.Name.Substring(1)

      Expect.isTrue
        (hexPart
         |> Seq.forall (fun ch -> "0123456789abcdef".Contains ch))
        "Hash part should be lowercase hex"
    }

    test "trims whitespace before hashing" {
      let a = Style.css "  color: red;  "
      let b = Style.css "color: red;"
      Expect.equal a.Name b.Name "Whitespace-padded body should match trimmed body"
    }

    test "ToString returns dot-prefixed class name" {
      let c = Style.css "color: red;"

      Expect.equal
        (string c)
        ("."
         + c.Name)
        "ToString should return .className"
    }

    test "can be interpolated in css for nesting" {
      let header = Style.css "color: red;"
      let container = Style.css $"{header} {{ font-size: 3rem; }}"
      Expect.notEqual header.Name container.Name "Container should be a different class"
    }
  ]

[<Tests>]
let styleGenerationTests =
  testList "Style generation" [
    testTask "renders style tag with correct css in html output" {
      let redText = Style.css "color: red;"

      let view =
        html [] [
          head [] [ Style.style ]
          body [] [ div [ Style._css redText ] [ str "Hello" ] ]
        ]

      let ctx = makeCtx ()
      let! _ = Style.html view next ctx
      let body = readBody ctx

      Expect.stringContains body Style.defaults.StyleId "Output should contain a style tag"
      Expect.stringContains body $".{redText.Name}" "Output should contain the class selector"
      Expect.stringContains body "color: red;" "Output should contain the CSS body"
    }

    testTask "does not render style tag when no css is used" {
      let view =
        html [] [
          head [] [ Style.style ]
          body [] [ div [] [ str "No styles" ] ]
        ]

      let ctx = makeCtx ()
      let! _ = Style.html view next ctx
      let body = readBody ctx

      Expect.isFalse (body.Contains "<style>") "Output should not contain a style tag"
    }
  ]

[<Tests>]
let pageScopingTests =
  testList "Page scoping" [
    testTask "different pages only render the css they use" {
      let redText = Style.css "color: red;"
      let blueText = Style.css "color: blue;"
      let _ = Style.css "color: green;"

      let viewA =
        html [] [
          head [] [ Style.style ]
          body [] [ div [ Style._css redText ] [ str "Page A" ] ]
        ]

      let ctxA = makeCtx ()
      let! _ = Style.html viewA next ctxA
      let bodyA = readBody ctxA

      let viewB =
        html [] [
          head [] [ Style.style ]
          body [] [ div [ Style._css blueText ] [ str "Page B" ] ]
        ]

      let ctxB = makeCtx ()
      let! _ = Style.html viewB next ctxB
      let bodyB = readBody ctxB

      // Page A has red only
      Expect.stringContains bodyA "color: red;" "Page A should contain red"
      Expect.isFalse (bodyA.Contains "color: blue;") "Page A should not contain blue"
      Expect.isFalse (bodyA.Contains "color: green;") "Page A should not contain green"

      // Page B has blue only
      Expect.stringContains bodyB "color: blue;" "Page B should contain blue"
      Expect.isFalse (bodyB.Contains "color: red;") "Page B should not contain red"
      Expect.isFalse (bodyB.Contains "color: green;") "Page B should not contain green"
    }

    testTask "page with multiple styles renders all of them" {
      let bold = Style.css "font-weight: bold;"
      let italic = Style.css "font-style: italic;"

      let view =
        html [] [
          head [] [ Style.style ]
          body [] [
            div [ Style._css bold ] [ str "Bold" ]
            div [ Style._css italic ] [ str "Italic" ]
          ]
        ]

      let ctx = makeCtx ()
      let! _ = Style.html view next ctx
      let body = readBody ctx

      Expect.stringContains body "font-weight: bold;" "Should contain bold style"
      Expect.stringContains body "font-style: italic;" "Should contain italic style"
    }
  ]

[<Tests>]
let globalStyleTests =
  testList "Global styles" [
    test "returns a class with Global scope" {
      let g = Style.globalStyle "body { margin: 0; }"
      Expect.equal g.Scope Style.Global "globalStyle should set Scope to Global"
    }

    test "class name starts with g" {
      let g = Style.globalStyle "body { margin: 0; }"
      Expect.isTrue (g.Name.StartsWith "g") "Global class name should start with 'g'"
    }

    testTask "emits raw CSS without selector wrapper" {
      let reset = Style.globalStyle "body { margin: 0; }"

      let view =
        html [] [
          head [] [ Style.style ]
          body [ Style._css reset ] [ str "Hello" ]
        ]

      let ctx = makeCtx ()
      let! _ = Style.html view next ctx
      let body = readBody ctx

      Expect.stringContains body "body { margin: 0; }" "Should contain raw global CSS"

      Expect.isFalse
        (body.Contains $".{reset.Name}{{")
        "Should not wrap global CSS in a class selector"
    }

    testTask "global styles appear before scoped styles" {
      let reset = Style.globalStyle "* { box-sizing: border-box; }"
      let redText = Style.css "color: red;"

      let view =
        html [] [
          head [] [ Style.style ]
          body [] [
            div [ Style._css redText ] [ str "Hello" ]
            div [ Style._css reset ] [ str "World" ]
          ]
        ]

      let ctx = makeCtx ()
      let! _ = Style.html view next ctx
      let body = readBody ctx

      let globalPos = body.IndexOf "* { box-sizing: border-box; }"
      let scopedPos = body.IndexOf $".{redText.Name}"

      Expect.isTrue
        (globalPos
         >= 0)
        "Should contain global CSS"

      Expect.isTrue
        (scopedPos
         >= 0)
        "Should contain scoped CSS"

      Expect.isTrue (globalPos < scopedPos) "Global CSS should appear before scoped CSS"
    }
  ]

[<Tests>]
let configTests =
  testList "Config" [
    test "custom class name length is used" {
      let s =
        Style.Engine.create {
          Style.defaults with
              ClassNameLength = 8
        }

      let c = s.css "color: red;"
      Expect.equal c.Name.Length 9 "Should be prefix (1) + 8 hex chars"
    }

    testTask "custom style id is used on the style tag" {
      let s =
        Style.Engine.create {
          Style.defaults with
              StyleId = "my-css"
        }

      let redText = s.css "color: red;"

      let view =
        html [] [
          head [] [ s.style ]
          body [] [ div [ s._css redText ] [ str "Hello" ] ]
        ]

      let ctx = makeCtx ()
      let! _ = s.html view next ctx
      let body = readBody ctx

      Expect.stringContains body "my-css" "Output should contain custom style id"
      Expect.stringContains body "color: red;" "Output should still render CSS"
    }
  ]

let private countOccurrences (sub: string) (s: string) =
  let mutable count = 0
  let mutable idx = s.IndexOf(sub)
  while idx >= 0 do
    count <- count + 1
    idx <- s.IndexOf(sub, idx + sub.Length)
  count

[<Tests>]
let deduplicationTests =
  testList "Deduplication" [
    testTask "same CSS used on multiple elements appears only once in output" {
      let s = Style.Engine.create Style.defaults
      let shared = s.css "color: red;"

      let view =
        html [] [
          head [] [ s.style ]
          body [] [
            div [ s._css shared ] [ str "One" ]
            p [ s._css shared ] [ str "Two" ]
            span [ s._css shared ] [ str "Three" ]
          ]
        ]

      let ctx = makeCtx ()
      let! _ = s.html view next ctx
      let body = readBody ctx

      Expect.stringContains body "color: red;" "Output should contain the CSS"
      let count = countOccurrences "color: red;" body
      Expect.equal count 1 "CSS body should appear exactly once even when class is used on 3 elements"
    }

    testTask "partials sharing styles produce only one copy of the CSS" {
      let s = Style.Engine.create Style.defaults
      let sharedClass = s.css "font-weight: bold;"

      let partialA () =
        div [ s._css sharedClass ] [ str "Partial A" ]

      let partialB () =
        section [ s._css sharedClass ] [ str "Partial B" ]

      let view =
        html [] [
          head [] [ s.style ]
          body [] [
            partialA ()
            partialB ()
          ]
        ]

      let ctx = makeCtx ()
      let! _ = s.html view next ctx
      let body = readBody ctx

      Expect.stringContains body "font-weight: bold;" "Output should contain the shared CSS"
      let count = countOccurrences "font-weight: bold;" body
      Expect.equal count 1 "Shared CSS from two partials should appear exactly once"
    }

    testTask "deduplication with cx produces no duplicate CSS" {
      let s = Style.Engine.create Style.defaults
      let shared = s.css "margin: 0;"
      let unique1 = s.css "padding: 1rem;"
      let unique2 = s.css "padding: 2rem;"

      let view =
        html [] [
          head [] [ s.style ]
          body [] [
            div [ s.cx [ shared; unique1 ] ] [ str "A" ]
            div [ s.cx [ shared; unique2 ] ] [ str "B" ]
          ]
        ]

      let ctx = makeCtx ()
      let! _ = s.html view next ctx
      let body = readBody ctx

      let marginCount = countOccurrences "margin: 0;" body
      Expect.equal marginCount 1 "Shared class CSS should appear exactly once despite being in two cx calls"
      Expect.stringContains body "padding: 1rem;" "Should contain first unique CSS"
      Expect.stringContains body "padding: 2rem;" "Should contain second unique CSS"
    }
  ]

[<Tests>]
let standaloneRenderTests =
  testList "Standalone render" [
    test "renders HTML with injected styles" {
      let s = Style.Engine.create Style.defaults
      let redText = s.css "color: red;"

      let view =
        html [] [
          head [] [ s.style ]
          body [] [ div [ s._css redText ] [ str "Hello" ] ]
        ]

      let result = s.render view

      Expect.stringContains result $".{redText.Name}" "Should contain the class selector"
      Expect.stringContains result "color: red;" "Should contain the CSS body"
      Expect.stringContains result "Hello" "Should contain the text content"
    }

    test "renders empty style tag when no classes used" {
      let s = Style.Engine.create Style.defaults

      let view =
        html [] [
          head [] [ s.style ]
          body [] [ div [] [ str "No styles" ] ]
        ]

      let result = s.render view

      Expect.stringContains result "No styles" "Should contain the text content"
      Expect.isFalse (result.Contains "color:") "Should not contain any CSS properties"
    }

    testTask "produces same output as HttpHandler" {
      let s = Style.Engine.create Style.defaults
      let redText = s.css "color: red;"

      let view =
        html [] [
          head [] [ s.style ]
          body [] [ div [ s._css redText ] [ str "Hello" ] ]
        ]

      let rendered = s.render view

      let ctx = makeCtx ()
      let! _ = s.html view next ctx
      let handled = readBody ctx

      Expect.equal rendered handled "render and html should produce identical output"
    }

    test "works with global styles" {
      let s = Style.Engine.create Style.defaults
      let reset = s.globalStyle "* { box-sizing: border-box; }"

      let view =
        html [] [
          head [] [ s.style ]
          body [ s._css reset ] [ str "Hello" ]
        ]

      let result = s.render view

      Expect.stringContains result "* { box-sizing: border-box; }" "Should contain raw global CSS"
    }
  ]

[<Tests>]
let sourceOrderedCssTests =
  testList "Source-ordered CSS" [
    testTask "styles appear in document order" {
      let s = Style.Engine.create Style.defaults
      let classA = s.css "color: red;"
      let classB = s.css "color: blue;"
      let classC = s.css "color: green;"

      let view =
        html [] [
          head [] [ s.style ]
          body [] [
            div [ s._css classC ] [ str "First" ]
            div [ s._css classA ] [ str "Second" ]
            div [ s._css classB ] [ str "Third" ]
          ]
        ]

      let ctx = makeCtx ()
      let! _ = s.html view next ctx
      let body = readBody ctx

      let posC = body.IndexOf $".{classC.Name}"
      let posA = body.IndexOf $".{classA.Name}"
      let posB = body.IndexOf $".{classB.Name}"

      Expect.isTrue (posC >= 0) "Should contain class C"
      Expect.isTrue (posA >= 0) "Should contain class A"
      Expect.isTrue (posB >= 0) "Should contain class B"
      Expect.isTrue (posC < posA) "C should appear before A (document order)"
      Expect.isTrue (posA < posB) "A should appear before B (document order)"
    }

    testTask "global styles still come before scoped" {
      let s = Style.Engine.create Style.defaults
      let scoped = s.css "color: red;"
      let glob = s.globalStyle "* { margin: 0; }"

      let view =
        html [] [
          head [] [ s.style ]
          body [] [
            div [ s._css scoped ] [ str "Scoped first" ]
            div [ s._css glob ] [ str "Global last" ]
          ]
        ]

      let ctx = makeCtx ()
      let! _ = s.html view next ctx
      let body = readBody ctx

      let posGlobal = body.IndexOf "* { margin: 0; }"
      let posScoped = body.IndexOf $".{scoped.Name}"

      Expect.isTrue (posGlobal >= 0) "Should contain global CSS"
      Expect.isTrue (posScoped >= 0) "Should contain scoped CSS"
      Expect.isTrue (posGlobal < posScoped) "Global CSS should appear before scoped CSS"
    }

    testTask "duplicate class usage preserves first occurrence order" {
      let s = Style.Engine.create Style.defaults
      let classA = s.css "font-weight: bold;"
      let classB = s.css "font-style: italic;"

      let view =
        html [] [
          head [] [ s.style ]
          body [] [
            div [ s._css classA ] [ str "A first" ]
            div [ s._css classB ] [ str "B second" ]
            div [ s._css classA ] [ str "A again" ]
          ]
        ]

      let ctx = makeCtx ()
      let! _ = s.html view next ctx
      let body = readBody ctx

      let posA = body.IndexOf $".{classA.Name}"
      let posB = body.IndexOf $".{classB.Name}"

      Expect.isTrue (posA >= 0) "Should contain class A"
      Expect.isTrue (posB >= 0) "Should contain class B"
      Expect.isTrue (posA < posB) "A should appear before B (first-seen order)"
    }
  ]

[<Tests>]
let nonceTests =
  testList "CSP nonce" [
    testTask "style tag includes nonce attribute" {
      let s = Style.Engine.create Style.defaults
      let redText = s.css "color: red;"

      let view =
        html [] [
          head [] [ s.styleWithNonce "abc123" ]
          body [] [ div [ s._css redText ] [ str "Hello" ] ]
        ]

      let ctx = makeCtx ()
      let! _ = s.html view next ctx
      let body = readBody ctx

      Expect.stringContains body "nonce=\"abc123\"" "Should contain the nonce attribute"
      Expect.stringContains body "color: red;" "Should still inject CSS"
    }

    test "styleWithNonce works with standalone render" {
      let s = Style.Engine.create Style.defaults
      let redText = s.css "color: red;"

      let view =
        html [] [
          head [] [ s.styleWithNonce "xyz789" ]
          body [] [ div [ s._css redText ] [ str "Hello" ] ]
        ]

      let result = s.render view

      Expect.stringContains result "nonce=\"xyz789\"" "Should contain the nonce attribute"
      Expect.stringContains result "color: red;" "Should still inject CSS"
    }

    testTask "different nonces per request" {
      let s = Style.Engine.create Style.defaults
      let redText = s.css "color: red;"

      let viewA =
        html [] [
          head [] [ s.styleWithNonce "nonce-a" ]
          body [] [ div [ s._css redText ] [ str "A" ] ]
        ]

      let viewB =
        html [] [
          head [] [ s.styleWithNonce "nonce-b" ]
          body [] [ div [ s._css redText ] [ str "B" ] ]
        ]

      let ctxA = makeCtx ()
      let! _ = s.html viewA next ctxA
      let bodyA = readBody ctxA

      let ctxB = makeCtx ()
      let! _ = s.html viewB next ctxB
      let bodyB = readBody ctxB

      Expect.stringContains bodyA "nonce=\"nonce-a\"" "Request A should have nonce-a"
      Expect.stringContains bodyB "nonce=\"nonce-b\"" "Request B should have nonce-b"
      Expect.isFalse (bodyA.Contains "nonce-b") "Request A should not have nonce-b"
      Expect.isFalse (bodyB.Contains "nonce-a") "Request B should not have nonce-a"
    }
  ]

[<EntryPoint>]
let main args = runTestsInAssemblyWithCLIArgs [] args
