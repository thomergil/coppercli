// Access token handling.
//
// The server prints a URL containing ?token=... . We keep that token for the tab and
// attach it to every /api/ call and to the WebSocket, so a page on some other site
// cannot drive the machine just because it can reach this port.
//
// The token is deliberately NOT stored in a cookie: a cookie is sent automatically with
// cross-site requests, which is exactly what makes an unauthenticated local server
// reachable from any page the operator happens to visit.

const STORAGE_KEY = 'coppercli_token';

function readToken() {
    const fromUrl = new URLSearchParams(window.location.search).get('token');

    if (fromUrl) {
        try {
            sessionStorage.setItem(STORAGE_KEY, fromUrl);
        } catch {
            // Private browsing can refuse storage; the in-memory copy still works.
        }
        // Drop it from the address bar so it does not linger in history, in the
        // Referer of any outbound link, or in a screenshot of the browser.
        try {
            const url = new URL(window.location.href);
            url.searchParams.delete('token');
            window.history.replaceState({}, '', url.toString());
        } catch {
            // Not fatal - the token still works from storage.
        }

        return fromUrl;
    }

    try {
        return sessionStorage.getItem(STORAGE_KEY) || '';
    } catch {
        return '';
    }
}

export const token = readToken();

/** Appends the token to a WebSocket URL. */
export function withToken(url) {
    if (!token) {
        return url;
    }
    return url + (url.includes('?') ? '&' : '?') + 'token=' + encodeURIComponent(token);
}

/**
 * Attaches the token to same-origin API calls. Done once here rather than at each of
 * every fetch site, so a new call cannot forget it.
 *
 * Called at module scope below: import statements are hoisted and evaluated in order,
 * so importing this module first is what guarantees the wrapper is in place before any
 * other module can issue a request.
 */
function installFetchAuth() {
    if (!token) {
        return;
    }

    const original = window.fetch;

    window.fetch = function (input, init) {
        const url = typeof input === 'string' ? input : (input && input.url) || '';

        if (url.startsWith('/api/')) {
            init = init ? { ...init } : {};
            init.headers = new Headers(init.headers || {});
            init.headers.set('Authorization', 'Bearer ' + token);
        }

        return original.call(this, input, init);
    };
}

installFetchAuth();
