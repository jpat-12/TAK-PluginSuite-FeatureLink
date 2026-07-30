<!-- Port of the ATAK plugin's ic_launcher.xml: same layered-map / feature-layer mark, same
     three fixed brand colors (not currentColor — this is the plugin's icon, not a
     theme-following UI glyph), just re-expressed as SVG paths instead of an Android vector. -->
<script setup lang="ts">
withDefaults(defineProps<{ size?: number | string }>(), { size: 24 });
</script>

<template>
    <svg :width='size' :height='size' viewBox='0 0 48 48' xmlns='http://www.w3.org/2000/svg'>
        <path fill='#0099CC' d='M24,40 L4,32 L24,24 L44,32 Z' />
        <path fill='#00BBEE' d='M24,32 L4,24 L24,16 L44,24 Z' />
        <path fill='#FFFFFF' d='M24,24 L4,16 L24,8 L44,16 Z' />
    </svg>
</template>
