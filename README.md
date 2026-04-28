# Giraffe.ViewEngine.Style

CSS-in-F# for [Giraffe.ViewEngine](https://github.com/giraffe-fsharp/Giraffe.ViewEngine). Define scoped styles inline, only the CSS actually used on each page gets rendered.

```fsharp
open Giraffe.ViewEngine

let heading = Style.css "font-size: 2rem; font-weight: bold;"
let accent = Style.css "color: coral;"

let view =
  html [] [
    head [] [ Style.style ]
    body [] [
      h1 [ Style.cx [ heading; accent ] ] [ str "Hello, world!" ]
      p [ Style._css accent ] [ str "Styled with F#." ]
    ]
  ]

let handler : HttpHandler = Style.html view
```

This renders a full HTML page where the `<style>` tag in the head contains only the CSS classes referenced in the view.

## Global styles

Use `Style.globalStyle` for rules that should be emitted without a class selector wrapper. Global styles are always rendered before scoped ones.

```fsharp
let reset = Style.globalStyle "* { box-sizing: border-box; margin: 0; padding: 0; }"
```

## Extending

`CssClass` implements `ToString()` returning the dot-prefixed selector, so you can interpolate classes into other CSS strings for nesting and composition:

```fsharp
let card = Style.css "padding: 1rem; border: 1px solid #ddd;"
let cardTitle = Style.css $"""
  {card} > h2 {{ 
    font-size: 1.5rem; 
  }}
"""
```

## Custom engines

The module-level functions (`Style.css`, `Style.html`, etc.) share a default global context. For isolated registries (for example, testing or multi-tenant setups) create your own engine:

```fsharp
let s = Style.Engine.create Style.defaults

let view =
  html [] [
    head [] [ s.style ]
    body [] [ div [ s._css (s.css "color: red;") ] [ str "Isolated" ] ]
  ]

let handler : HttpHandler = s.html view
```

## Configuration

Pass a custom `Config` to `Engine.create` to control class name generation:

```fsharp
let s =
  Style.Engine.create {
    Style.defaults with
      StyleId = "my-app-css"       // id attribute on the <style> tag
      ClassNameLength = 8          // hex characters in the hash (default: 12)
  }
```

## CSP nonce

If your site uses a [Content Security Policy](https://developer.mozilla.org/en-US/docs/Web/HTTP/CSP), use `styleWithNonce` instead of `style` to add a per-request nonce to the `<style>` tag:

```fsharp
let nonce = generateNonce () // your per-request nonce

let view =
  html [] [
    head [] [ Style.styleWithNonce nonce ]
    body [] [ h1 [ Style._css heading ] [ str "Hello" ] ]
  ]
```

This renders `<style id="styled-giraffe-css" nonce="...">` so the inline styles pass the CSP check.

## How it works

1. `Style.css` hashes the CSS body and registers it in a concurrent dictionary under a generated class name (e.g. `c1a2b3c4d5e6`).
2. `Style.style` places an empty `<style>` tag in the document head as an injection point.
3. `Style.html` walks the view tree, collects which registered class names are actually used, builds the CSS string, injects it into the `<style>` tag, and writes the response.

## Installation

```
dotnet add package Giraffe.ViewEngine.Style
```

Requires `Giraffe` and `Giraffe.ViewEngine`.
