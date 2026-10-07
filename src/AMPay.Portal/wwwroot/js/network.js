/* Copied from AMPay.Web/wwwroot/js/network.js - the same signature on the console and the portal. Keep the two in step. */
/* ==========================================================================
   The "live network" dot field from the AM-Pay public site (hero-network-bg),
   ported so the console and the site share one signature.

   Any <canvas data-network> is brought to life:
     data-network="interactive"  denser field, twinkle and glow; dots flee the
                                 cursor and link to it. Used where there is room.
     data-network="ambient"      the site's quiet interior-page look: sparse,
                                 slow, no cursor. Used behind working screens.
     data-tone="dark" | "light"  white links and bright green dots for navy
                                 backgrounds; navy links and green dots for light.

   Unlike the site, it stops drawing while off screen or in a background tab,
   and with prefers-reduced-motion draws one still frame instead of nothing -
   the dots are part of the look, only the movement is optional.
   ========================================================================== */
(function () {
    'use strict';

    var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    var canHover = window.matchMedia('(hover: hover)').matches;

    function start(canvas) {
        var interactive = canvas.dataset.network === 'interactive';
        var cursor = interactive && canHover && !reduceMotion;
        var onDark = canvas.dataset.tone !== 'light';

        var lineColor = onDark ? 'rgba(255,255,255,A)' : 'rgba(30,54,116,A)';
        var dotColor = onDark ? 'rgba(109,218,155,0.75)' : 'rgba(74,170,60,0.55)';
        var cursorColor = 'rgba(109,218,155,A)';

        var host = canvas.parentElement;
        var glow = host.querySelector('.network-glow');
        var ctx = canvas.getContext('2d');
        var width = 0, height = 0, nodes = [], t = 0;
        var mouse = { x: null, y: null };
        var running = false, visible = true, frame = 0;

        function size() {
            var rect = host.getBoundingClientRect();
            var dpr = Math.min(window.devicePixelRatio || 1, 2);
            width = rect.width;
            height = rect.height;
            canvas.width = Math.round(width * dpr);
            canvas.height = Math.round(height * dpr);
            canvas.style.width = width + 'px';
            canvas.style.height = height + 'px';
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        }

        function seed() {
            var divisor = interactive ? 9000 : 16000;
            var cap = interactive ? 85 : 40;
            var count = Math.max(14, Math.min(cap, Math.round((width * height) / divisor)));
            nodes = [];
            for (var i = 0; i < count; i++) {
                nodes.push({
                    x: Math.random() * width,
                    y: Math.random() * height,
                    vx: (Math.random() - 0.5) * 0.28,
                    vy: (Math.random() - 0.5) * 0.28,
                    r: Math.random() * 1.6 + 1,
                    phase: Math.random() * Math.PI * 2,
                    twinkle: 0.6 + Math.random() * 0.9
                });
            }
        }

        function move() {
            nodes.forEach(function (n) {
                if (cursor && mouse.x !== null) {
                    var dx = n.x - mouse.x, dy = n.y - mouse.y;
                    var dist = Math.sqrt(dx * dx + dy * dy) || 1;
                    if (dist < 130) {
                        var force = (130 - dist) / 130 * 0.9;
                        n.vx += (dx / dist) * force;
                        n.vy += (dy / dist) * force;
                    }
                }

                n.x += n.vx;
                n.y += n.vy;

                if (cursor) {
                    n.vx *= 0.965;
                    n.vy *= 0.965;
                    var speed = Math.sqrt(n.vx * n.vx + n.vy * n.vy);
                    if (speed > 2.6) { n.vx = n.vx / speed * 2.6; n.vy = n.vy / speed * 2.6; }
                    if (speed < 0.12) { n.vx += (Math.random() - 0.5) * 0.04; n.vy += (Math.random() - 0.5) * 0.04; }
                }

                if (n.x < 0 || n.x > width) n.vx *= -1;
                if (n.y < 0 || n.y > height) n.vy *= -1;
            });
        }

        function draw() {
            ctx.clearRect(0, 0, width, height);

            var linkDist = interactive ? 165 : 140;
            var lineAlpha = interactive ? 0.26 : (onDark ? 0.22 : 0.16);

            for (var i = 0; i < nodes.length; i++) {
                for (var j = i + 1; j < nodes.length; j++) {
                    var a = nodes[i], b = nodes[j];
                    var dx = a.x - b.x, dy = a.y - b.y;
                    var dist = Math.sqrt(dx * dx + dy * dy);
                    if (dist < linkDist) {
                        ctx.strokeStyle = lineColor.replace('A', ((1 - dist / linkDist) * lineAlpha).toFixed(3));
                        ctx.lineWidth = 1;
                        ctx.beginPath();
                        ctx.moveTo(a.x, a.y);
                        ctx.lineTo(b.x, b.y);
                        ctx.stroke();
                    }
                }
            }

            if (cursor && mouse.x !== null) {
                nodes.forEach(function (n) {
                    var dx = n.x - mouse.x, dy = n.y - mouse.y;
                    var dist = Math.sqrt(dx * dx + dy * dy);
                    if (dist < 190) {
                        ctx.strokeStyle = cursorColor.replace('A', ((1 - dist / 190) * 0.55).toFixed(3));
                        ctx.lineWidth = 1.1;
                        ctx.beginPath();
                        ctx.moveTo(mouse.x, mouse.y);
                        ctx.lineTo(n.x, n.y);
                        ctx.stroke();
                    }
                });

                ctx.beginPath();
                ctx.arc(mouse.x, mouse.y, 3, 0, Math.PI * 2);
                ctx.fillStyle = 'rgba(109,218,155,0.9)';
                ctx.shadowBlur = 12;
                ctx.shadowColor = 'rgba(109,218,155,0.9)';
                ctx.fill();
                ctx.shadowBlur = 0;
            }

            nodes.forEach(function (n) {
                // The shine: each dot breathes on its own phase.
                var flicker = interactive ? 0.7 + 0.3 * Math.sin(t * n.twinkle + n.phase) : 1;
                ctx.beginPath();
                ctx.arc(n.x, n.y, n.r, 0, Math.PI * 2);
                ctx.fillStyle = dotColor;
                ctx.globalAlpha = flicker;
                if (interactive) { ctx.shadowBlur = 5; ctx.shadowColor = dotColor; }
                ctx.fill();
                ctx.shadowBlur = 0;
                ctx.globalAlpha = 1;
            });
        }

        function tick() {
            if (!running) return;
            t += 0.016;
            move();
            draw();
            frame = requestAnimationFrame(tick);
        }

        function update() {
            var shouldRun = !reduceMotion && visible && !document.hidden;
            if (shouldRun && !running) {
                running = true;
                frame = requestAnimationFrame(tick);
            } else if (!shouldRun && running) {
                running = false;
                cancelAnimationFrame(frame);
            }
        }

        size();
        seed();
        draw();

        if ('IntersectionObserver' in window) {
            new IntersectionObserver(function (entries) {
                visible = entries[0].isIntersecting;
                update();
            }).observe(host);
        }
        document.addEventListener('visibilitychange', update);
        update();

        var resizeTimer;
        var lastWidth = width, lastHeight = height;
        function onResize() {
            clearTimeout(resizeTimer);
            resizeTimer = setTimeout(function () {
                size();
                // A sidebar that grows by a few pixels keeps its dots; only a real
                // change of shape reseeds the field.
                if (Math.abs(width - lastWidth) > 40 || Math.abs(height - lastHeight) > 120) {
                    seed();
                    lastWidth = width;
                    lastHeight = height;
                }
                draw();
            }, 150);
        }
        if ('ResizeObserver' in window) new ResizeObserver(onResize).observe(host);
        else window.addEventListener('resize', onResize);

        if (cursor) {
            host.addEventListener('mousemove', function (e) {
                var rect = canvas.getBoundingClientRect();
                mouse.x = e.clientX - rect.left;
                mouse.y = e.clientY - rect.top;
                if (glow) {
                    glow.style.setProperty('--mx', (mouse.x / rect.width * 100).toFixed(1) + '%');
                    glow.style.setProperty('--my', (mouse.y / rect.height * 100).toFixed(1) + '%');
                }
            });
            host.addEventListener('mouseleave', function () { mouse.x = mouse.y = null; });
        }
    }

    function init() {
        document.querySelectorAll('canvas[data-network]').forEach(function (canvas) {
            if (!canvas.getContext) return;
            start(canvas);
        });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
