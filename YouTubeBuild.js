// Builds Mizar's YouTube ad blocking from uBlock Origin's own code and filter lists.
// Runs once per update in a hidden WebView, as a module page at https://ytbuild.mizar.internal/
// (a folder mapping of <data>\ytblock\work). Uses uBO's parser and scriptlet maker unchanged, so
// rules are read and scriptlets assembled exactly as uBO does, then keeps only YouTube's rules.
// Result: window.__ytResult = { main, isolated, css: [...], stats } or { error }.

import './ubo/lib/regexanalyzer/regex.js';
import { AstFilterParser, utils } from './ubo/js/static-filtering-parser.js';
import * as makeScriptlets from './ubo/js/make-scriptlets.js';
import { builtinScriptlets } from './ubo/js/resources/scriptlets.js';

const LISTS = ['lists/filters.txt', 'lists/quick-fixes.txt', 'lists/unbreak.txt', 'lists/easylist.txt'];
const ENV = ['chromium', 'ublock'];
const isYouTube = hn => /(^|\.)(youtube|youtube-nocookie)\.(com|\*)$/.test(hn);

const text = async path => {
    const r = await fetch(path, { cache: 'no-store' });
    if (!r.ok) throw new Error(`${path}: ${r.status}`);
    return r.text();
};

const stripHeader = s => s.replace(/^\/\*[\s\S]*?\*\/\s*/, '');

async function build() {
    const [ tplCore, tplBegin, tplEnd ] = await Promise.all([
        text('ubo/js/scriptlet.template.js'),
        text('ubo/js/scriptlet-begin.template.js'),
        text('ubo/js/scriptlet-end.template.js'),
    ].map(p => p.then(stripHeader)));

    const parser = new AstFilterParser({ trustedSource: true, nativeCssHas: true });
    const scriptlets = [];        // { args, hns, nots }
    const scriptletExceptions = new Set(); // `${hn}\n${json args}` or `${hn}\n*`
    const cosmetics = [];         // { css, hns }
    const cosmeticExceptions = new Set(); // `${hn}\n${raw selector}`
    let skippedCosmetic = 0;

    for (const path of LISTS) {
        const raw = await text(path);
        const pruned = utils.preparser.prune(raw, ENV);
        for (const line of pruned.split(/\r?\n/)) {
            if (line.includes('youtube') === false) continue;
            parser.parse(line);
            if (parser.hasError()) continue;
            const isScriptlet = parser.isScriptletFilter();
            const isCosmetic = parser.isCosmeticFilter();
            if (!isScriptlet && !isCosmetic) continue;
            // the iterator reuses one object for every item: copy the fields
            const domains = [];
            for (const { hn, not, bad } of parser.getExtFilterDomainIterator()) if (!bad) domains.push({ hn, not });
            const hns = domains.filter(d => !d.not && isYouTube(d.hn)).map(d => d.hn);
            if (hns.length === 0) continue;
            const nots = domains.filter(d => d.not).map(d => d.hn);
            if (isScriptlet) {
                const args = parser.getScriptletArgs();
                if (parser.isException()) {
                    for (const hn of hns) scriptletExceptions.add(`${hn}\n${args.length ? JSON.stringify(args) : '*'}`);
                } else if (args.length) {
                    scriptlets.push({ args, hns, nots });
                }
                continue;
            }
            const sel = parser.result.raw;
            if (parser.isException()) {
                for (const hn of hns) cosmeticExceptions.add(`${hn}\n${sel}`);
                continue;
            }
            const compiled = parser.result.compiled;
            let css;
            if (typeof compiled === 'string' && compiled.startsWith('{') === false) {
                css = `${compiled}{display:none!important}`;
            } else {
                try {
                    const o = JSON.parse(compiled);
                    if (o.tasks === undefined && o.action?.[0] === 'style') css = `${o.selector}{${o.action[1]}}`;
                    else if (o.tasks === undefined && o.action?.[0] === 'remove') css = `${o.selector}{display:none!important}`;
                } catch { }
            }
            if (css === undefined) { skippedCosmetic++; continue; }
            cosmetics.push({ css, hns, sel });
        }
    }

    makeScriptlets.init(builtinScriptlets);
    let compiledCount = 0;
    for (const s of scriptlets) {
        const key = JSON.stringify(s.args);
        const hns = s.hns.filter(hn => !scriptletExceptions.has(`${hn}\n${key}`) && !scriptletExceptions.has(`${hn}\n*`));
        if (hns.length === 0) continue;
        makeScriptlets.compile('uBlock', {
            args: s.args.slice(),
            matches: hns,
            excludeMatches: s.nots.length ? s.nots : undefined,
            trustedSource: true,
        });
        compiledCount++;
    }

    const assemble = world => {
        const secret = crypto.randomUUID();
        const core = makeScriptlets.commit('uBlock', tplCore, secret)[world];
        if (!core) return '';
        const fw = makeScriptlets.commitFramework(tplBegin, tplEnd, secret)[world];
        return [
            `(function uBO_${world}() {`,
            'const $SCRIPTLET_GLOBALS$ = {};',
            fw.beginCode, core.code, fw.endCode,
            '})();',
        ].join('\n');
    };

    const css = [];
    for (const c of cosmetics) {
        if (c.hns.every(hn => cosmeticExceptions.has(`${hn}\n${c.sel}`))) continue;
        css.push(c.css);
    }

    return {
        main: assemble('MAIN'),
        isolated: assemble('ISOLATED'),
        css: Array.from(new Set(css)),
        stats: { scriptlets: compiledCount, cosmetic: css.length, skippedCosmetic },
    };
}

build().then(r => { window.__ytResult = r; }, e => { window.__ytResult = { error: String(e && e.stack || e) }; });
