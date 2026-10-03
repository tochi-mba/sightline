/*
  A stand-in for a browser page, small enough to read in one sitting. It parses the site's own
  index.html into elements that keep their attributes, children and listeners, so the page tests run
  app.js against the real markup with no browser and no packages.

  It understands the HTML this site writes and is strict about it: an element left open, or closed
  out of order, is an error, so markup a browser would quietly repair fails the tests instead. It
  implements only what app.js uses, and a selector or an entity it does not know is an error rather
  than an empty answer.
*/

const VOID = new Set(["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta"]);
const RAW_TEXT = new Set(["script", "style"]);
const ENTITIES = new Map([["amp", "&"], ["lt", "<"], ["gt", ">"], ["quot", '"'], ["apos", "'"]]);

// One token at a time: a comment, the doctype, a closing tag, or an opening tag with its attributes
// and an optional self-closing slash (which the SVG inside the page uses).
const ATTRIBUTE_NAME = String.raw`[^\s"'>/=]+`;
const ATTRIBUTE_VALUE = String.raw`(?:"[^"]*"|'[^']*'|[^\s"'>]+)`;
const TOKEN = [
  String.raw`<!--[\s\S]*?-->`,
  String.raw`<!doctype[^>]*>`,
  String.raw`<\/([a-z][\w-]*)\s*>`,
  String.raw`<([a-z][\w-]*)((?:\s+${ATTRIBUTE_NAME}(?:\s*=\s*${ATTRIBUTE_VALUE})?)*)\s*(\/?)>`,
].join("|");
const ATTRIBUTE = /([^\s"'>/=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+)))?/g;

function decode(text) {
  return text.replace(/&(#x[0-9a-f]+|#[0-9]+|[a-z]+);/gi, (whole, name) => {
    if (/^#x/i.test(name)) return String.fromCodePoint(parseInt(name.slice(2), 16));
    if (name.startsWith("#")) return String.fromCodePoint(parseInt(name.slice(1), 10));
    if (!ENTITIES.has(name)) throw new Error(`the stand-in page does not know ${whole}`);
    return ENTITIES.get(name);
  });
}

export class StandInEvent {
  constructor(type, init = {}) {
    this.type = type;
    this.key = init.key;
    this.target = null;
    this.defaultPrevented = false;
  }

  preventDefault() {
    this.defaultPrevented = true;
  }
}

function listen(listeners, type, listener) {
  if (!listeners.has(type)) listeners.set(type, []);
  listeners.get(type).push(listener);
}

function call(listeners, event) {
  for (const listener of listeners.get(event.type) ?? []) listener(event);
}

class Text {
  constructor(data) {
    this.data = data;
    this.parentNode = null;
  }

  get textContent() {
    return this.data;
  }
}

class ClassList {
  constructor(element) {
    this.element = element;
  }

  get names() {
    return (this.element.getAttribute("class") ?? "").split(/\s+/).filter((name) => name !== "");
  }

  contains(name) {
    return this.names.includes(name);
  }

  toggle(name, force) {
    const on = force === undefined ? !this.contains(name) : Boolean(force);
    const names = this.names.filter((existing) => existing !== name);
    if (on) names.push(name);
    this.element.setAttribute("class", names.join(" "));
    return on;
  }
}

class Element {
  constructor(page, localName, attributes) {
    this.page = page;
    this.localName = localName;
    this.attributes = new Map(attributes);
    this.childNodes = [];
    this.parentNode = null;
    this.listeners = new Map();
  }

  get id() {
    return this.getAttribute("id") ?? "";
  }

  getAttribute(name) {
    return this.attributes.has(name) ? this.attributes.get(name) : null;
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  removeAttribute(name) {
    this.attributes.delete(name);
  }

  hasAttribute(name) {
    return this.attributes.has(name);
  }

  get hidden() {
    return this.hasAttribute("hidden");
  }

  set hidden(value) {
    if (value) this.setAttribute("hidden", "");
    else this.removeAttribute("hidden");
  }

  get classList() {
    return new ClassList(this);
  }

  get children() {
    return this.childNodes.filter((node) => node instanceof Element);
  }

  get textContent() {
    return this.childNodes.map((node) => node.textContent).join("");
  }

  set textContent(value) {
    for (const node of this.childNodes) node.parentNode = null;
    this.childNodes = [];
    this.append(new Text(String(value)));
  }

  /** Moves each node here, out of wherever it was, as the DOM's append does. */
  append(...nodes) {
    for (const node of nodes) {
      if (node.parentNode !== null) {
        const siblings = node.parentNode.childNodes;
        siblings.splice(siblings.indexOf(node), 1);
      }
      node.parentNode = this;
      this.childNodes.push(node);
    }
  }

  contains(node) {
    for (let at = node; at !== null; at = at.parentNode) if (at === this) return true;
    return false;
  }

  querySelectorAll(selector) {
    if (!/^[a-z][a-z0-9-]*$/.test(selector)) {
      throw new Error(`the stand-in page matches tag names only, not "${selector}"`);
    }
    const found = [];
    const walk = (element) => {
      for (const child of element.children) {
        if (child.localName === selector) found.push(child);
        walk(child);
      }
    };
    walk(this);
    return found;
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] ?? null;
  }

  addEventListener(type, listener) {
    listen(this.listeners, type, listener);
  }

  /** Runs the listeners here, then on each ancestor, then on the document, as a bubbling event. */
  dispatchEvent(event) {
    event.target = this;
    for (let at = this; at !== null; at = at.parentNode) call(at.listeners, event);
    call(this.page.listeners, event);
    return !event.defaultPrevented;
  }

  click() {
    return this.dispatchEvent(new StandInEvent("click"));
  }

  focus() {
    this.page.activeElement = this;
    this.dispatchEvent(new StandInEvent("focusin"));
  }
}

class Page {
  constructor() {
    this.listeners = new Map();
    this.activeElement = null;
    this.root = new Element(this, "#document", []);
  }

  get documentElement() {
    return this.root.children[0];
  }

  getElementById(id) {
    const search = (element) => {
      for (const child of element.children) {
        if (child.id === id) return child;
        const deeper = search(child);
        if (deeper !== null) return deeper;
      }
      return null;
    };
    return search(this.root);
  }

  addEventListener(type, listener) {
    listen(this.listeners, type, listener);
  }
}

/** The page a browser would build from this HTML, or an error naming the line that is wrong. */
export function parsePage(html) {
  const page = new Page();
  const open = [page.root];
  const current = () => open[open.length - 1];
  const lineAt = (index) => html.slice(0, index).split("\n").length;
  const token = new RegExp(TOKEN, "gi");
  let cursor = 0;
  for (let match = token.exec(html); match !== null; match = token.exec(html)) {
    if (match.index > cursor) current().append(new Text(decode(html.slice(cursor, match.index))));
    cursor = token.lastIndex;
    const [, closing, opening, attributes, selfClosing] = match;
    if (closing !== undefined) {
      const name = closing.toLowerCase();
      if (current().localName !== name) {
        throw new Error(`line ${lineAt(match.index)}: </${name}> closes <${current().localName}>`);
      }
      open.pop();
      continue;
    }
    if (opening === undefined) continue; // A comment or the doctype.
    const name = opening.toLowerCase();
    const values = [...attributes.matchAll(ATTRIBUTE)].map(([, key, double, single, bare]) => [
      key.toLowerCase(),
      decode(double ?? single ?? bare ?? ""),
    ]);
    const element = new Element(page, name, values);
    current().append(element);
    if (RAW_TEXT.has(name)) {
      const end = html.toLowerCase().indexOf(`</${name}>`, cursor);
      if (end === -1) throw new Error(`line ${lineAt(match.index)}: <${name}> is never closed`);
      element.append(new Text(html.slice(cursor, end)));
      cursor = end + name.length + 3;
      token.lastIndex = cursor;
    } else if (!VOID.has(name) && selfClosing === "") {
      open.push(element);
    }
  }
  if (cursor < html.length) current().append(new Text(decode(html.slice(cursor))));
  if (open.length !== 1) throw new Error(`<${current().localName}> is never closed`);
  return page;
}

/** The parts of a browser window app.js reads, with the device and the network chosen by the test. */
export function standInWindow({ userAgent = "", userAgentData, maxTouchPoints = 0, hash = "", fetch }) {
  const listeners = new Map();
  return {
    navigator: { userAgent, userAgentData, maxTouchPoints },
    location: { hash },
    fetch,
    addEventListener(type, listener) {
      listen(listeners, type, listener);
    },
    dispatch(type) {
      call(listeners, new StandInEvent(type));
    },
  };
}
