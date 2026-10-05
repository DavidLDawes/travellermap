import './setup.js';
import assert from 'node:assert/strict';
import {test} from 'node:test';
import {serviceBase} from '../../map.js';

const at = url => new URL(url);

test('at the root of a domain, API URLs start at the root', () => {
  assert.equal(serviceBase(at('https://travellermap.com/'), 'https://travellermap.com/map.js?update=1'), '');
  assert.equal(serviceBase(at('https://travellermap.srd-tools.com/make/poster'), 'https://travellermap.srd-tools.com/map.js'), '');
});

test('mounted under a path, API URLs start at the mount', () => {
  assert.equal(serviceBase(at('https://srd-tools.com/TravellerMap/'), 'https://srd-tools.com/TravellerMap/map.js?update=1'), '/TravellerMap');
  // Pages in subdirectories import ../map.js, which is still at the site root.
  assert.equal(serviceBase(at('https://srd-tools.com/TravellerMap/print/world?sector=spin'), 'https://srd-tools.com/TravellerMap/map.js'), '/TravellerMap');
});

test('local development still uses travellermap.com', () => {
  assert.equal(serviceBase(at('http://localhost/~me/travellermap/'), 'http://localhost/~me/travellermap/map.js'), 'https://travellermap.com');
  assert.equal(serviceBase(at('file:///C:/travellermap/index.html'), 'file:///C:/travellermap/map.js'), 'https://travellermap.com');
});

test('a module from another origin or a non-web URL means the domain root', () => {
  assert.equal(serviceBase(at('https://example.com/page'), 'https://cdn.example.net/lib/map.js'), '');
  assert.equal(serviceBase(at('https://travellermap.com/'), 'file:///C:/repo/map.js'), '', 'as under Node');
  assert.equal(serviceBase(at('https://travellermap.com/'), 'not a url'), '');
});
