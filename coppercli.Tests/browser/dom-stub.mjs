import { readFileSync } from 'node:fs';

// Enough of a page for the status modules to run against. Elements are created on demand
// and kept, so a test can read back what the code wrote.

class ClassList {
    constructor() { this._names = new Set(); }
    add(...names) { names.forEach(n => this._names.add(n)); }
    remove(...names) { names.forEach(n => this._names.delete(n)); }
    contains(name) { return this._names.has(name); }
    toggle(name, on) { if (on) { this.add(name); } else { this.remove(name); } }
}

// Text assigned through textContent becomes a text node, so markup in it is shown rather
// than parsed. Modelled, because a label written over an icon is the defect under test.
const escapeMarkup = text =>
    text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const unescapeMarkup = text =>
    text.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');

class El {
    constructor(id = '', className = '') {
        this.id = id;
        this.classList = new ClassList();
        this.children = [];
        this.dataset = {};
        this.style = {};
        this._html = '';
        this.className = className;
        this.disabled = false;
        this.hidden = false;
        this.value = '';
    }
    // textContent and innerHTML are one field in a browser: writing either replaces
    // everything inside the element. Kept separate here, a test could not see an icon being
    // destroyed by a label change.
    get innerHTML() { return this._html; }
    set innerHTML(value) { this._html = String(value); this.children.length = 0; }
    get textContent() { return unescapeMarkup(this._html.replace(/<[^>]*>/g, '')); }
    set textContent(value) { this._html = escapeMarkup(String(value)); this.children.length = 0; }

    // Handlers are kept so a test can drive a control the page binds this way, rather than
    // through the onclick property.
    addEventListener(type, handler) { (this.handlers ??= {})[type] = handler; }

    // Fire the handler bound for this event, whichever way it was bound. Throws when
    // nothing is bound, so a test cannot pass by never reaching the code it names.
    fire(type) {
        const handler = this.handlers?.[type] ?? (type === 'click' ? this.onclick : null);
        if (!handler) {
            throw new Error(`nothing is bound for ${type} on #${this.id}`);
        }
        return handler({ preventDefault() { } });
    }
    appendChild(child) { this.children.push(child); child.parent = this; return child; }
    remove() { this.parent?.children.splice(this.parent.children.indexOf(this), 1); }
    querySelector(selector) {
        const wanted = selector.replace(/^\./, '');
        return this.children.find(c => c.classList.contains(wanted) || c.className === wanted) ?? null;
    }
    querySelectorAll() { return []; }
    setAttribute(name, value) { this[name] = value; }
    removeAttribute(name) { delete this[name]; }
}

const elements = new Map();
const lists = new Map();

// Ids come from the page, not from the test. Otherwise an element the code asks for that
// index.html does not have would be created here and the mismatch never noticed.
const pageIds = new Set(
    [...readFileSync(new URL('../../coppercli/WebServer/wwwroot/index.html', import.meta.url), 'utf8')
        .matchAll(/id="([A-Za-z0-9_-]+)"/g)].map(m => m[1]));

// Empty, byId would reject every element and the stub would be useless - but a test that
// only reads would still pass. Fail here instead, where the cause is visible.
if (pageIds.size === 0) {
    throw new Error('no element ids found on index.html; the stub page would reject everything');
}

function byId(id) {
    if (!pageIds.has(id)) {
        throw new Error(`the browser writes to #${id}, which index.html does not have`);
    }
    if (!elements.has(id)) { elements.set(id, new El(id)); }
    return elements.get(id);
}

function list(selector, items) {
    lists.set(selector, items);
    return items;
}

const document = {
    getElementById: byId,
    querySelector: () => null,
    querySelectorAll: selector => lists.get(selector) ?? [],
    createElement: () => new El(),
    addEventListener: () => { },
    body: new El('body')
};

// The one way a browser test reaches a wwwroot module, so the three test files do not each
// spell the path out.
export async function load(module) {
    return import(`../../coppercli/WebServer/wwwroot/js/${module}`);
}

/// The toast most recently shown, or undefined if none was.
export function lastToast() {
    return globalThis.document.body.children.at(-1);
}

function jogButton(axis) {
    const btn = new El();
    btn.dataset.axis = axis;
    return btn;
}

function modeButton(mode) {
    const btn = new El();
    btn.dataset.mode = mode;
    return btn;
}

// Installs the stub as the page and returns the handles tests read back.
export function installDom() {
    elements.clear();
    lists.clear();
    // The body is created once, so toasts from an earlier test would still be on it and a
    // test reading the last one would read someone else's.
    document.body.children.length = 0;

    const jogButtons = list('.jog-btn[data-axis]', [jogButton('X'), jogButton('Y'), jogButton('Z')]);
    const modeButtons = list('.mode-btn[data-mode]', [modeButton('step'), modeButton('continuous')]);
    list('.screen', []);

    globalThis.document = document;
    globalThis.window = { addEventListener: () => { }, location: { href: '' } };

    return { el: byId, jogButtons, modeButtons };
}
