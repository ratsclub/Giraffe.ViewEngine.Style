namespace Giraffe.ViewEngine


[<RequireQualifiedAccess>]
module Style =
  open System.Collections.Concurrent
  open Giraffe
  open Giraffe.ViewEngine
  open System.Security.Cryptography
  open System.Text
  open System.Collections.Generic

  /// Whether a CSS rule is scoped to a generated class name or emitted globally.
  type CssScope =
    | Global
    | Scoped

  /// A registered CSS class. ToString() returns the dot-prefixed selector (e.g. ".c1a2b3"),
  /// so it can be interpolated directly into other CSS strings for extending/nesting.
  type CssClass = {
    Name: string
    Scope: CssScope
    Body: string
  } with

    override this.ToString() =
      "."
      + this.Name

  /// A function that produces the final class name from a hash, scope, and CSS body.
  type CssPrefixer = string -> CssScope -> string -> string

  type private ClassRegistry = ConcurrentDictionary<string, CssClass>

  let private createClassRegistry () = ClassRegistry()

  /// Configuration for a StyleEngine instance.
  type Config = {
    /// The HTML id attribute placed on the injected style tag.
    StyleId: string
    /// Produces the final class name from a hash, scope, and CSS body.
    ClassPrefixer: CssPrefixer
    /// Number of hex characters in the generated hash (must be even).
    ClassNameLength: int
  }

  let private hashClassName (length: int) (s: string) =
    use sha = SHA256.Create()

    sha.ComputeHash(Encoding.UTF8.GetBytes s)
    |> Array.take (length / 2)
    |> Array.map (sprintf "%02x")
    |> String.concat ""

  let private registerClass
    (registry: ClassRegistry)
    hash
    (prefixer: CssPrefixer)
    scope
    (body: string)
    =
    let body = body.Trim()
    let h = hash body
    let name = prefixer h scope body

    let cssClass = {
      Name = name
      Scope = scope
      Body = body
    }

    registry.TryAdd(name, cssClass)
    |> ignore

    cssClass

  let private hasId (id: string) (attrs: XmlAttribute[]) =
    attrs
    |> Array.exists (
      function
      | KeyValue("id", v) -> v = id
      | _ -> false
    )

  let private collectClasses (registry: ClassRegistry) tree =
    let seen = HashSet<string>()
    let ordered = ResizeArray<string>()

    let collect attrs =
      for attr in attrs do
        match attr with
        | KeyValue("class", v) ->
          for name in v.Split(' ') do
            if registry.ContainsKey name && seen.Add name then
              ordered.Add name
        | _ -> ()

    let rec walk =
      function
      | ParentNode((_, attrs), children) ->
        collect attrs
        List.iter walk children
      | VoidElement(_, attrs) -> collect attrs
      | Text _ -> ()

    walk tree
    ordered

  let private injectStyle (registry: ClassRegistry) styledId (used: ResizeArray<string>) tree =
    let buildStyleContent () =
      let globalString = StringBuilder()
      let scopedString = StringBuilder()

      for name in used do
        match registry.TryGetValue name with
        | true, { Body = body; Scope = Global } ->
          globalString.Append body
          |> ignore
        | true, { Body = body; Scope = Scoped } ->
          scopedString.Append('.').Append(name).Append('{').Append(body).Append('}')
          |> ignore
        | false, _ -> ()

      globalString.Append scopedString
      |> ignore

      globalString.ToString()

    let rec inject =
      function
      | ParentNode((tag, attrs), _) when
        tag = "style"
        && hasId styledId attrs
        ->
        let content =
          if used.Count > 0 then
            [ Text(buildStyleContent ()) ]
          else
            []

        ParentNode((tag, attrs), content)
      | ParentNode((tag, attrs), children) -> ParentNode((tag, attrs), List.map inject children)
      | other -> other

    inject tree

  let private prefixer hash scope _ =
    let prefix =
      match scope with
      | Scoped -> "c"
      | Global -> "g"

    sprintf "%s%s" prefix hash

  /// Default configuration: 12-character hex hash, "c"/"g" prefix, and "styled-giraffe-css" style id.
  let defaults = {
    StyleId = "styled-giraffe-css"
    ClassPrefixer = prefixer
    ClassNameLength = 12
  }

  /// An isolated style context with its own registry. Created via Engine.create.
  type StyleEngine = {
    /// Registers a scoped CSS rule and returns its generated class.
    css: string -> CssClass
    /// Registers a global CSS rule (emitted without a class selector wrapper).
    globalStyle: string -> CssClass
    /// Produces a class attribute from a CssClass.
    _css: CssClass -> XmlAttribute
    /// Combines multiple CssClass values into a single class attribute.
    cx: CssClass list -> XmlAttribute
    /// The style placeholder node to place in the document head.
    style: XmlNode
    /// Creates a style placeholder node with a CSP nonce attribute.
    styleWithNonce: string -> XmlNode
    /// Giraffe HttpHandler that collects used classes, injects styles, and writes the response.
    html: XmlNode -> HttpHandler
    /// Renders styled HTML to a string without requiring a Giraffe HttpHandler.
    render: XmlNode -> string
  }

  module Engine =
    /// Creates an isolated StyleEngine with its own CSS registry.
    let create (config: Config) : StyleEngine =
      let ctxRegistry = createClassRegistry ()
      let ctxHash = hashClassName config.ClassNameLength

      let css body =
        registerClass ctxRegistry ctxHash config.ClassPrefixer Scoped body

      let globalStyle body =
        registerClass ctxRegistry ctxHash config.ClassPrefixer Global body

      let _css c = _class c.Name

      let cx classes =
        classes
        |> List.map (fun c -> c.Name)
        |> String.concat " "
        |> _class

      let style = HtmlElements.style [ _id config.StyleId ] []

      let styleWithNonce (nonce: string) =
        HtmlElements.style [ _id config.StyleId; attr "nonce" nonce ] []

      let html view : HttpHandler =
        fun _ ctx ->
          let used = collectClasses ctxRegistry view
          let view = injectStyle ctxRegistry config.StyleId used view
          let bytes = RenderView.AsBytes.htmlDocument view
          ctx.SetContentType "text/html; charset=utf-8"
          ctx.WriteBytesAsync bytes

      let render view =
        let used = collectClasses ctxRegistry view
        let view = injectStyle ctxRegistry config.StyleId used view
        RenderView.AsString.htmlDocument view

      {
        css = css
        globalStyle = globalStyle
        _css = _css
        cx = cx
        style = style
        styleWithNonce = styleWithNonce
        html = html
        render = render
      }

  let private defaultEngine = Engine.create defaults

  /// Registers a scoped CSS rule and returns its generated class.
  let css = defaultEngine.css

  /// Registers a global CSS rule (emitted without a class selector wrapper).
  let globalStyle = defaultEngine.globalStyle

  /// Produces a class attribute from a CssClass.
  let _css = defaultEngine._css

  /// The style placeholder node to place in the document head.
  let style = defaultEngine.style

  /// Creates a style placeholder node with a CSP nonce attribute.
  let styleWithNonce = defaultEngine.styleWithNonce

  /// Combines multiple CssClass values into a single class attribute.
  let cx = defaultEngine.cx

  /// Giraffe HttpHandler that collects used classes, injects styles, and writes the response.
  let html = defaultEngine.html

  /// Renders styled HTML to a string without requiring a Giraffe HttpHandler.
  let render = defaultEngine.render
