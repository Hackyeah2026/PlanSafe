import test from 'node:test';
import assert from 'node:assert/strict';
import { initCitizenMap, updateCitizenMap, disposeCitizenMap } from '../src/PlanSafe.App/wwwroot/js/citizenMapInterop.js';

test('missing or outside location focuses the first zone without refitting on every update', () => {
    const fits = [];
    const fitOptions = [];
    const map = { zoomControl: { setPosition() {} }, on() {}, invalidateSize() {}, remove() {},
        fitBounds(bounds, options) { fits.push(bounds); fitOptions.push(options); } };
    const layer = () => ({ items: [], addTo() { return this; }, clearLayers() { this.items = []; },
        getLayers() { return this.items; } });
    globalThis.document = { getElementById: () => ({}) };
    globalThis.window = {};
    globalThis.L = {
        map: () => map,
        tileLayer: () => ({ addTo() {} }),
        layerGroup: layer,
        polygon: points => ({ addTo(group) { group.items.push(this); return this; }, getBounds: () => points }),
        featureGroup: () => ({ getBounds: () => ({ isValid: () => true }) }),
    };
    const zones = [[[50, 19], [50, 20], [51, 20]], [[52, 21], [52, 22], [53, 22]]];
    const update = needsLocation => updateCitizenMap('test', null, null, null, '[]', null, '[]', '[]', JSON.stringify(zones), '[]', needsLocation);
    try {
        initCitizenMap('test');
        update(true);
        assert.deepEqual(fits, [zones[0]]);
        assert.deepEqual(fitOptions[0], { padding: [24, 24], maxZoom: 19 });
        update(true);
        assert.equal(fits.length, 1);
        update(false);
        update(true);
        assert.deepEqual(fits.at(-1), zones[0]);
        assert.equal(fits.length, 2);
    } finally {
        disposeCitizenMap('test');
        delete globalThis.L;
        delete globalThis.window;
        delete globalThis.document;
    }
});
