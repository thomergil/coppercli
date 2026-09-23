import { readFileSync } from 'node:fs';

// A stub page for the wwwroot modules to run against under node --test. Elements are created
// on demand and kept, so a test can read back what the code wrote; their ids come from
// index.html, so a write to an id the page does not have throws instead of creating one.

class ClassList {
    constructor() { this._names = new Set(); }
    add(...names) { names.forEach(n => this._names.add(n)); }
    remove(...names) { names.forEach(n => this._names.delete(n)); }
    contains(name) { return this._names.has(name); }
    toggle(name, on) { if (on) { this.add(name); } else { this.remove(name); } }
}

// Text assigned through textContent becomes a text node, so markup in it is shown rather
// than parsed. Modeled here because a label written over an icon is the defect under test.
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
    // everything inside the element. Kept as one field here, so a test sees an icon removed
    // by a label change.
    get innerHTML() { return this._html; }
    set innerHTML(value) { this._html = String(value); this.children.length = 0; }
    get textContent() { return unescapeMarkup(this._html.replace(/<[^>]*>/g, '')); }
    set textContent(value) { this._html = escapeMarkup(String(value)); this.children.length = 0; }

    addEventListener(type, handler) { (this.handlers ??= {})[type] = handler; }

    // Throws when nothing is bound for the event, so a test cannot pass without reaching the
    // code it names.
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
    // A real input moves keyboard focus; nothing here reads it back, so there is nothing to
    // model beyond accepting the call the save screen makes on its filename field.
    focus() { }
}

const elements = new Map();
const lists = new Map();

const pageIds = new Set(
    [...readFileSync(new URL('../../coppercli/WebServer/wwwroot/index.html', import.meta.url), 'utf8')
        .matchAll(/id="([A-Za-z0-9_-]+)"/g)].map(m => m[1]));

// With no ids, byId rejects every element the code asks for. Throw here, where the cause is
// visible, rather than at the first getElementById.
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

export async function load(module) {
    return import(`../../coppercli/WebServer/wwwroot/js/${module}`);
}

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

export function installDom() {
    elements.clear();
    lists.clear();
    // The body is created once for the module, so without this a test reading the last toast
    // reads one an earlier test left.
    document.body.children.length = 0;

    const jogButtons = list('.jog-btn[data-axis]', [jogButton('X'), jogButton('Y'), jogButton('Z')]);
    const modeButtons = list('.mode-btn[data-mode]', [modeButton('step'), modeButton('continuous')]);
    list('.screen', []);

    globalThis.document = document;
    globalThis.window = { addEventListener: () => { }, location: { href: '' } };

    return { el: byId, jogButtons, modeButtons };
}

const UNTIL_POLL_MS = 10;
const UNTIL_TIMEOUT_MS = 2000;

/**
 * Polls condition until it holds, for a test that waits on a module's own async work rather
 * than on an awaitable it returns. Throws, rather than returning false, so a test that forgets
 * to check the result still fails instead of passing on a state it never reached.
 */
export async function until(condition, what) {
    const started = Date.now();
    while (!condition()) {
        if (Date.now() - started >= UNTIL_TIMEOUT_MS) {
            throw new Error(`timed out waiting for ${what}`);
        }
        await new Promise(resolve => setTimeout(resolve, UNTIL_POLL_MS));
    }
}

// The buttons block every status payload carries; each test overrides only the fields it
// needs. Present by default so updateStatus takes every branch it can: the stub checks an id
// only when the code that writes it runs.
export const BUTTONS = {
    jog: { enabled: true },
    probe: { enabled: true },
    mill: { enabled: true },
    // Disabled by default: with no door hold there is nothing to release.
    doorRelease: { enabled: false }
};

/** The default /api/status payload, for a test that only cares about a few fields. */
export function payload(overrides) {
    return {
        connected: true,
        status: 'Idle',
        machineActivity: 'Idle',
        needsAttention: false,
        canPause: false,
        canResume: false,
        machineUnavailable: false,
        canReleaseDoor: false,
        doorMessage: null,
        buttons: BUTTONS,
        workPos: { x: 0, y: 0, z: 0 },
        machinePos: { x: 0, y: 0, z: 0 },
        feedOverride: 100,
        probePin: false,
        depthAdjustment: 0,
        file: { currentLine: 0, totalLines: 0 },
        probe: { state: 'none', total: 0, progress: 0 },
        ...overrides
    };
}
