// The file list is built as markup from names on disk. A name carrying a quote used to end
// the attribute holding its path, so the file could not be loaded; an angle bracket
// corrupted the rest of the list.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installDom, load } from './dom-stub.mjs';

const { FileBrowser, escapeMarkup } = await load('helpers.js');

test('a name with a quote does not end the attribute holding its path', () => {
    const dom = installDom();
    const browser = new FileBrowser({
        listElementId: 'file-list',
        pathElementId: 'current-path',
        apiEndpoint: '/api/files',
        fileIcon: '',
        metaField: 'size'
    });

    browser.render({
        currentPath: '/boards',
        entries: [{ path: '/boards/a"b.ngc', name: 'a"b.ngc', isDir: false, size: 1 }]
    });

    const drawn = dom.el('file-list').innerHTML;

    assert.match(drawn, /data-path="[^"]*&quot;[^"]*"/,
        'the quote in the name closed the attribute early');
    assert.doesNotMatch(drawn, /data-path="\/boards\/a"/,
        'the raw quote reached the markup');
});

test('a name with an angle bracket is shown, not parsed', () => {
    const dom = installDom();
    assert.equal(escapeMarkup('<b>x</b>'), '&lt;b&gt;x&lt;/b&gt;');
    assert.equal(escapeMarkup('a & b'), 'a &amp; b');
});
