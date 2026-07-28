<script setup lang="ts">
withDefaults(defineProps<{ size?: number | string }>(), { size: 24 });
</script>

<template>
    <svg :width='size' :height='size' viewBox='0 0 24 24' fill='none' xmlns='http://www.w3.org/2000/svg'>
        <circle cx='12' cy='12' r='10' stroke='currentColor' stroke-width='1.5' />
        <path d='M7 14.5 L12 6 L17 14.5' stroke='currentColor' stroke-width='1.5' stroke-linejoin='round' fill='none' />
        <circle cx='7' cy='16' r='1.6' fill='currentColor' />
        <circle cx='12' cy='16' r='1.6' fill='currentColor' />
        <circle cx='17' cy='16' r='1.6' fill='currentColor' />
    </svg>
</template>
