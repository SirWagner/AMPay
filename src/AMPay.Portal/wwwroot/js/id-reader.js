/*
 * South African ID: fill the form from the ID number, and read the ID document itself.
 *
 * 1. As the ID number is typed it is checked (date and check digit) and the date of birth,
 *    gender and citizenship it carries are filled in - a field is only overwritten if it was
 *    empty or filled in by this script, never if a person typed it.
 * 2. A photo or scan of the ID document is read in this browser: first the barcode (the smart
 *    ID card carries name, surname, gender and ID number in its PDF417 barcode; the green ID book
 *    a barcode of the ID number), then, if that fails, the printed text. Nothing is uploaded:
 *    the picture never leaves the browser and is not stored.
 *
 * Shared by AM-Pay (staff capture) and the self-service portal. The same rules as
 * AMPay.Domain.Validation.SaIdNumber, which stays the authority on the server.
 *
 * Markup: a form with data-id-form, fields marked data-id-field="idNumber|dateOfBirth|gender|
 * nationality|firstName|middleNames|surname|isSaId", an element data-id-status for the ID line,
 * and for the reader a file input data-id-reader-file, a button data-id-reader-go and an
 * element data-id-reader-status.
 */
(function () {
    'use strict';

    var LIBS = {
        zxing: 'https://cdn.jsdelivr.net/npm/@zxing/library@0.21.3/umd/index.min.js',
        tesseract: 'https://cdn.jsdelivr.net/npm/tesseract.js@6.0.1/dist/tesseract.min.js',
        pdf: 'https://cdn.jsdelivr.net/npm/pdfjs-dist@4.10.38/build/pdf.min.mjs',
        pdfWorker: 'https://cdn.jsdelivr.net/npm/pdfjs-dist@4.10.38/build/pdf.worker.min.mjs'
    };
    var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

    // ------------------------------------------------------------------ the number

    function luhn(digits) {
        var sum = 0, dbl = false;
        for (var i = digits.length - 1; i >= 0; i--) {
            var d = digits.charCodeAt(i) - 48;
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d; dbl = !dbl;
        }
        return sum % 10 === 0;
    }

    /** Everything a 13-digit SA ID number says, or why it is not one. */
    function parse(raw) {
        var digits = String(raw || '').replace(/\D/g, '');
        if (digits.length !== 13) return { valid: false, digits: digits, reason: digits.length ? 'An SA ID number has 13 digits (' + digits.length + ' so far).' : null };

        var yy = +digits.substr(0, 2), mm = +digits.substr(2, 2), dd = +digits.substr(4, 2);
        var now = new Date();
        var year = (2000 + yy > now.getFullYear() ? 1900 : 2000) + yy;
        var dob = new Date(Date.UTC(year, mm - 1, dd));
        if (mm < 1 || mm > 12 || dob.getUTCMonth() !== mm - 1 || dob.getUTCDate() !== dd)
            return { valid: false, digits: digits, reason: 'The first six digits are not a real date of birth.' };
        if (!luhn(digits))
            return { valid: false, digits: digits, reason: 'The check digit does not match: a digit is wrong or two are swapped.' };

        var age = now.getFullYear() - year;
        if (now.getMonth() < mm - 1 || (now.getMonth() === mm - 1 && now.getDate() < dd)) age--;
        var c = digits.charAt(10);

        return {
            valid: true,
            digits: digits,
            dateOfBirth: year + '-' + pad(mm) + '-' + pad(dd),
            dateText: dd + ' ' + MONTHS[mm - 1] + ' ' + year,
            age: age,
            gender: +digits.charAt(6) < 5 ? 'Female' : 'Male',
            citizen: c === '0' ? true : c === '1' ? false : null
        };
    }

    function pad(n) { return (n < 10 ? '0' : '') + n; }

    // ------------------------------------------------------------------ filling the form

    function field(form, name) { return form.querySelector('[data-id-field="' + name + '"]'); }

    /** Sets a field unless a person typed something else into it. Returns true when it changed. */
    function put(el, value) {
        if (!el || value == null || value === '') return false;
        var auto = el.dataset.idAuto === '1';
        if (el.value && !auto && el.value !== value) return false;
        if (el.tagName === 'SELECT' && !Array.prototype.some.call(el.options, function (o) { return o.value === value; })) return false;
        if (el.value === value) { el.dataset.idAuto = '1'; return false; }
        el.value = value;
        el.dataset.idAuto = '1';
        el.classList.add('id-autofilled');
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
    }

    function bindNumber(form) {
        var input = field(form, 'idNumber'), status = form.querySelector('[data-id-status]'), isSa = field(form, 'isSaId');
        if (!input) return;

        // Typing in a filled field makes it the person's own: never overwrite it again.
        form.addEventListener('input', function (e) {
            if (e.target !== input && e.target.dataset && e.target.dataset.idAuto === '1') {
                e.target.dataset.idAuto = '';
                e.target.classList.remove('id-autofilled');
            }
        });

        function update() {
            if (isSa && !isSa.checked) { if (status) status.textContent = ''; return; }
            var r = parse(input.value);
            if (!status) { }
            else if (!r.digits) { status.textContent = ''; status.className = 'hint'; }
            else if (!r.valid) { status.textContent = r.reason || ''; status.className = 'hint id-status-bad'; }
            else {
                status.textContent = 'Valid SA ID: born ' + r.dateText + ' (' + r.age + '), ' + r.gender.toLowerCase() +
                    (r.citizen === true ? ', South African citizen' : r.citizen === false ? ', permanent resident' : '') +
                    (r.age < 18 ? '. Under 18: cannot borrow.' : '.');
                status.className = 'hint id-status-good';
            }
            if (!r.valid) return;
            put(field(form, 'dateOfBirth'), r.dateOfBirth);
            put(field(form, 'gender'), r.gender);
            var nat = field(form, 'nationality');
            if (r.citizen === true) put(nat, 'South African');
            else if (r.citizen === false && nat && nat.value === 'South African') { nat.value = ''; nat.placeholder = 'Permanent resident: country of citizenship'; }
        }

        input.addEventListener('input', update);
        if (isSa) isSa.addEventListener('change', update);
        update();
    }

    // ------------------------------------------------------------------ reading the document

    var loaded = {};
    function loadScript(src) {
        if (!loaded[src]) loaded[src] = new Promise(function (resolve, reject) {
            var s = document.createElement('script');
            s.src = src; s.async = true; s.crossOrigin = 'anonymous';
            s.onload = resolve; s.onerror = function () { reject(new Error('Could not load ' + src)); };
            document.head.appendChild(s);
        });
        return loaded[src];
    }

    /** The file as a canvas: a photo as it is, a PDF scan as its first page. */
    async function toCanvas(file) {
        var canvas = document.createElement('canvas');
        if (file.type === 'application/pdf' || /\.pdf$/i.test(file.name)) {
            var pdfjs = await import(LIBS.pdf);
            pdfjs.GlobalWorkerOptions.workerSrc = LIBS.pdfWorker;
            var doc = await pdfjs.getDocument({ data: new Uint8Array(await file.arrayBuffer()) }).promise;
            var page = await doc.getPage(1);
            var vp = page.getViewport({ scale: 2.5 });
            canvas.width = vp.width; canvas.height = vp.height;
            await page.render({ canvasContext: canvas.getContext('2d'), viewport: vp }).promise;
            return canvas;
        }
        var url = URL.createObjectURL(file);
        try {
            var img = await new Promise(function (resolve, reject) {
                var i = new Image(); i.onload = function () { resolve(i); }; i.onerror = function () { reject(new Error('That file is not a picture this browser can open.')); }; i.src = url;
            });
            var scale = Math.min(1, 2400 / Math.max(img.width, img.height));
            canvas.width = Math.round(img.width * scale); canvas.height = Math.round(img.height * scale);
            canvas.getContext('2d').drawImage(img, 0, 0, canvas.width, canvas.height);
            return canvas;
        } finally { URL.revokeObjectURL(url); }
    }

    /** The barcode's text, or null. The browser's own reader first where it has one. */
    async function readBarcode(canvas) {
        if ('BarcodeDetector' in window) {
            try {
                var formats = await window.BarcodeDetector.getSupportedFormats();
                var wanted = ['pdf417', 'code_39', 'code_128', 'qr_code'].filter(function (f) { return formats.indexOf(f) >= 0; });
                if (wanted.length) {
                    var found = await new window.BarcodeDetector({ formats: wanted }).detect(canvas);
                    if (found.length) return found[0].rawValue;
                }
            } catch (e) { /* fall through to the library */ }
        }
        await loadScript(LIBS.zxing);
        var Z = window.ZXing;
        var hints = new Map();
        hints.set(Z.DecodeHintType.POSSIBLE_FORMATS, [Z.BarcodeFormat.PDF_417, Z.BarcodeFormat.CODE_39, Z.BarcodeFormat.CODE_128, Z.BarcodeFormat.QR_CODE]);
        hints.set(Z.DecodeHintType.TRY_HARDER, true);
        var reader = new Z.BrowserMultiFormatReader(hints);
        try {
            var img = new Image();
            img.src = canvas.toDataURL('image/png');
            await img.decode();
            return (await reader.decodeFromImageElement(img)).getText();
        } catch (e) { return null; }
    }

    /**
     * What a barcode says. The smart ID card's PDF417 is pipe-separated: surname, names, sex,
     * nationality, ID number, then dates. Read by position around the ID number, defensively,
     * since the layout is not published.
     */
    function fromBarcode(text) {
        if (!text) return null;
        var parts = String(text).split('|').map(function (p) { return p.trim(); });
        var at = -1;
        for (var i = 0; i < parts.length; i++) if (/^\d{13}$/.test(parts[i]) && parse(parts[i]).valid) { at = i; break; }
        if (at < 0) {
            var only = String(text).replace(/\D/g, '');
            return only.length === 13 && parse(only).valid ? { idNumber: only, source: 'barcode' } : null;
        }
        var r = { idNumber: parts[at], source: 'barcode' };
        if (at >= 2 && /[A-Za-z]/.test(parts[0]) && /[A-Za-z]/.test(parts[1])) { r.surname = parts[0]; r.names = parts[1]; }
        var sex = parts.slice(0, at).find(function (p) { return /^(M|F)$/i.test(p); });
        if (sex) r.gender = /^m$/i.test(sex) ? 'Male' : 'Female';
        return r;
    }

    /** What the printed text says: the ID number (only one that validates), and the names under their labels. */
    function fromText(text) {
        var r = { source: 'text' };

        var runs = String(text).match(/[0-9OoIl|SsBZ][0-9OoIl|SsBZ ]{11,22}[0-9OoIl|SsBZ]/g) || [];
        for (var i = 0; i < runs.length && !r.idNumber; i++) {
            var d = runs[i].replace(/ /g, '').replace(/[Oo]/g, '0').replace(/[Il|]/g, '1').replace(/[Ss]/g, '5').replace(/B/g, '8').replace(/Z/g, '2');
            if (/^\d+$/.test(d)) for (var k = 0; k + 13 <= d.length; k++) {
                var cand = d.substr(k, 13);
                if (parse(cand).valid) { r.idNumber = cand; break; }
            }
        }

        var lines = String(text).split(/\r?\n/).map(function (l) { return l.trim(); }).filter(Boolean);
        var isLabel = function (l) { return /surname|names|voorname|sex|geslag|nationality|identity|date of birth|country|status|id no/i.test(l); };
        var nameLike = function (l) { return /^[A-Za-z][A-Za-z' -]{1,60}$/.test(l) && !isLabel(l); };
        function after(label, not) {
            for (var i = 0; i < lines.length; i++) {
                if (!label.test(lines[i]) || (not && not.test(lines[i]))) continue;
                var rest = lines[i].replace(label, '').replace(/^[^A-Za-z]+/, '').trim();
                if (nameLike(rest)) return rest;
                for (var j = i + 1; j < Math.min(lines.length, i + 3); j++) if (nameLike(lines[j])) return lines[j];
            }
            return null;
        }
        r.surname = after(/(van\s*\/\s*)?surname/i);
        r.names = after(/(voorname\s*\/\s*)?(fore)?names/i, /surname/i);
        var sex = /\b(sex|geslag)\b[^A-Za-z]*([MF])\b/i.exec(text);
        if (sex) r.gender = sex[2].toUpperCase() === 'M' ? 'Male' : 'Female';
        return r;
    }

    /** MOLEFE -> Molefe, VAN DER MERWE -> Van der Merwe: particles after the first word stay small. */
    var PARTICLES = ['van', 'der', 'den', 'de', 'du', 'la', 'le', 'von', 'te', 'ter'];
    function title(s) {
        return String(s || '').toLowerCase().replace(/\s+/g, ' ').trim().split(' ').map(function (w, i) {
            if (i > 0 && PARTICLES.indexOf(w) >= 0) return w;
            return w.replace(/(^|['-])([a-z])/g, function (m, a, b) { return a + b.toUpperCase(); });
        }).join(' ');
    }

    function bindReader(form) {
        var file = form.querySelector('[data-id-reader-file]'), go = form.querySelector('[data-id-reader-go]');
        var status = form.querySelector('[data-id-reader-status]');
        if (!file) return;

        function say(text, tone) { if (status) { status.textContent = text; status.className = 'hint ' + (tone || ''); } }

        async function run() {
            var f = file.files && file.files[0];
            if (!f) { say('Choose a photo or scan of the ID first.', 'id-status-bad'); return; }
            if (go) go.disabled = true;
            try {
                say('Opening the picture...');
                var canvas = await toCanvas(f);

                say('Looking for the barcode...');
                var result = fromBarcode(await readBarcode(canvas));

                // The barcode of a green ID book holds only the number: read the names off the page too.
                if (!result || !result.surname) {
                    say('Reading the printed text. This takes a few seconds the first time...');
                    await loadScript(LIBS.tesseract);
                    var ocr = await window.Tesseract.recognize(canvas, 'eng');
                    var text = fromText(ocr.data.text);
                    result = result ? Object.assign(text, result, { surname: text.surname, names: text.names, source: 'barcode and text' }) : text;
                }

                var filled = apply(form, result || {});
                if (!result || !result.idNumber) {
                    say((filled ? 'Filled ' + filled + ' field(s), but no valid ID number was found. ' : 'Nothing could be read. ') +
                        'Try a sharper, straight-on photo in good light, or type the details.', 'id-status-bad');
                } else {
                    say('Filled ' + filled + ' field(s) from the ' + result.source + '. Check every highlighted field against the ID before saving.', 'id-status-good');
                }
            } catch (e) {
                say('The ID could not be read: ' + (e && e.message ? e.message : e) + ' Type the details instead.', 'id-status-bad');
            } finally {
                if (go) go.disabled = false;
            }
        }

        if (go) go.addEventListener('click', run); else file.addEventListener('change', run);
    }

    /** Puts what was read into the form; returns how many fields changed. */
    function apply(form, r) {
        var n = 0;
        var isSa = field(form, 'isSaId');
        if (r.idNumber && isSa && !isSa.checked) { isSa.checked = true; isSa.dispatchEvent(new Event('change', { bubbles: true })); }
        if (r.idNumber) {
            var id = field(form, 'idNumber');
            if (id && id.value.replace(/\D/g, '') !== r.idNumber) { id.value = r.idNumber; id.classList.add('id-autofilled'); n++; }
            if (id) id.dispatchEvent(new Event('input', { bubbles: true }));
        }
        if (r.surname && put(field(form, 'surname'), title(r.surname))) n++;
        if (r.names) {
            var names = title(r.names).split(' ');
            if (put(field(form, 'firstName'), names[0])) n++;
            if (names.length > 1 && put(field(form, 'middleNames'), names.slice(1).join(' '))) n++;
        }
        if (r.gender && put(field(form, 'gender'), r.gender)) n++;
        // Count every field now showing something read or worked out, the ID's own fills included.
        return n ? form.querySelectorAll('.id-autofilled').length : 0;
    }

    window.SaId = { parse: parse, fromBarcode: fromBarcode, fromText: fromText };

    document.querySelectorAll('[data-id-form]').forEach(function (form) {
        bindNumber(form);
        bindReader(form);
    });
})();
