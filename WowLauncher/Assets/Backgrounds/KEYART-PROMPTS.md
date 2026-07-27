# Key-Art — Generier-Prompts (Stonetavern Launcher, v3-Hero)

> Slot: `/AI/projects/wow/launcher/WowLauncher/Assets/Backgrounds/hero-<realmId>.webp`, 2000×1280.
> Doktrin: `HANDOFF-CLIENT-2026-07-21.md` §1.3. Erzeugt über ChatGPT-Bildgenerierung.
> 🔴 Niemals Blizzard-Assets, niemals gefundene Fremdbilder — nur eigene, generische Fantasy-Landschaft.
> Fremde Referenzbilder (Screenshots, Fan-Art) dienen ausschließlich als **Stimmungsvorlage**, sie
> werden nie ausgeliefert.
>
> Nachbearbeitung nach dem Download:
> `magick <src> -resize 2000x1280^ -gravity center -extent 2000x1280 -quality 88 hero-<realm>.webp`

## Warum der Negativ-Block Pflicht ist

GPT-image hat **kein** echtes Negative-Prompt-Feld — die Negation muss **im Prompt stehen**, sonst
kommt der generische AI-Look zurück (übersättigt, HDR-Glow, goldene Stunde, Plastik-3D-Glanz). Runde 1
am 2026-07-20 war genau dieser Slop. Der Block unten wird **wortwörtlich** mitgeschickt, in beiden
Prompts, unverkürzt.

### Der Negativ-Block (identisch in jedem Prompt)

```
Deliberately avoid all of the following, they make the image look AI-generated:
oversaturated colour, high dynamic range, HDR glow, strong golden-hour sunlight, visible sun disc,
sun flare, lens flare, bloom, god rays, rim light on everything, glossy plastic 3D-render sheen,
raytraced reflections, hyperreal micro detail, over-sharpened edges, airbrushed gradients,
teal-and-orange colour grading, cinematic postcard look, dramatic epic lighting, perfect symmetry,
vignette, depth-of-field blur, chromatic aberration, digital noise, tilt-shift.
No text, no lettering, no signature, no watermark, no logo, no UI, no frame, no border.
No people, no figures, no animals, no creatures, no vehicles.
```

### Der Stil-Anker (identisch in jedem Prompt)

```
Hand-painted fantasy matte painting in the tradition of classic concept art.
It is NOT a 3D render and NOT a photograph. Visible painterly brushwork, dry-brush texture,
matte finish, muted and slightly desaturated palette, low contrast, soft atmospheric perspective,
large calm areas with very little detail. Environment art only.
Wide cinematic landscape, aspect ratio 25:16, 2000x1280.
Keep the LOWER LEFT third calm, dark and uncluttered so overlaid text stays readable.
```

---

## Elwynn (`hero-elwynn.webp`)

Lichtstimmung: **ruhiger bewölkter Morgen, kühl grün-grau, kein direktes Sonnenlicht.**
Owner-Vorgabe: das **blaue Haus** muss drin sein.

```
Hand-painted fantasy matte painting in the tradition of classic concept art.
It is NOT a 3D render and NOT a photograph. Visible painterly brushwork, dry-brush texture,
matte finish, muted and slightly desaturated palette, low contrast, soft atmospheric perspective,
large calm areas with very little detail. Environment art only.
Wide cinematic landscape, aspect ratio 25:16, 2000x1280.
Keep the LOWER LEFT third calm, dark and uncluttered so overlaid text stays readable.

Scene: a quiet temperate forest valley on a calm overcast morning. Cool grey-green light under a
heavy soft cloud ceiling, no direct sunlight, no visible sun. Tall old oaks with dense dark foliage
frame the left edge. In the middle distance, nestled between the trees, stands a small half-timbered
cottage with a STEEP BLUE SLATE ROOF, pale plaster walls and dark timber beams, two small windows
lit faintly warm from inside, a stone chimney with thin grey smoke. A low wooden fence and a narrow
dirt path lead toward it. A shallow river winds from the middle distance down toward the lower right,
reflecting the pale grey sky. Rolling green meadows, hedgerows and a second blue-roofed farmhouse far
off, distant blue-grey wooded hills fading into morning haze on the horizon.

Deliberately avoid all of the following, they make the image look AI-generated:
oversaturated colour, high dynamic range, HDR glow, strong golden-hour sunlight, visible sun disc,
sun flare, lens flare, bloom, god rays, rim light on everything, glossy plastic 3D-render sheen,
raytraced reflections, hyperreal micro detail, over-sharpened edges, airbrushed gradients,
teal-and-orange colour grading, cinematic postcard look, dramatic epic lighting, perfect symmetry,
vignette, depth-of-field blur, chromatic aberration, digital noise, tilt-shift.
No text, no lettering, no signature, no watermark, no logo, no UI, no frame, no border.
No people, no figures, no animals, no creatures, no vehicles.
```

---

## Barrens (`hero-barrens.webp`)

Lichtstimmung: **trockene Mittagshitze unter Staubdunst, ocker, flaches Licht.**
Owner-Vorgabe: der **hölzerne Wachturm auf Stelzen** muss drin sein.

```
Hand-painted fantasy matte painting in the tradition of classic concept art.
It is NOT a 3D render and NOT a photograph. Visible painterly brushwork, dry-brush texture,
matte finish, muted and slightly desaturated palette, low contrast, soft atmospheric perspective,
large calm areas with very little detail. Environment art only.
Wide cinematic landscape, aspect ratio 25:16, 2000x1280.
Keep the LOWER LEFT third calm, dark and uncluttered so overlaid text stays readable.

Scene: a dry ocher savanna under hazy midday heat. Flat dusty bright-overcast sky, thick dust haze,
no visible sun, no long shadows. Cracked red-brown earth, pale dry grass tufts and scattered
flat-topped acacia trees. In the middle distance stands a WEATHERED WOODEN WATCHTOWER built on four
tall crossbraced stilt legs, with a simple pitched shingle roof, an open railed platform and a
leaning ladder, standing alone and silhouetted against the dust haze. A dry eroded gully cuts from
the lower right toward the middle distance. A large flat-topped sandstone mesa rises on the right and
fades into the haze, low ridges on the far horizon.

Deliberately avoid all of the following, they make the image look AI-generated:
oversaturated colour, high dynamic range, HDR glow, strong golden-hour sunlight, visible sun disc,
sun flare, lens flare, bloom, god rays, rim light on everything, glossy plastic 3D-render sheen,
raytraced reflections, hyperreal micro detail, over-sharpened edges, airbrushed gradients,
teal-and-orange colour grading, cinematic postcard look, dramatic epic lighting, perfect symmetry,
vignette, depth-of-field blur, chromatic aberration, digital noise, tilt-shift.
No text, no lettering, no signature, no watermark, no logo, no UI, no frame, no border.
No people, no figures, no animals, no creatures, no vehicles.
```
