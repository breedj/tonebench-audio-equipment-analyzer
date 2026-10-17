<!--
  Prints a message that carries backtick-marked code spans -- the simulation import and
  re-analysis services write their SPICE directives that way, and the dialogs used to print the
  backticks themselves. `inlineCodeSegments` holds the reading of them, and has the tests; this is
  only the styling, which is a translucent tint rather than a neutral one because these messages
  sit on amber and red boxes and the manual's own opaque `code` background fights both.
-->
<script setup lang="ts">
import { computed } from 'vue'

import { inlineCodeSegments } from '../inlineCode'

const props = defineProps<{ text: string }>()

const segments = computed(() => inlineCodeSegments(props.text))
</script>

<template>
  <template v-for="(segment, index) in segments" :key="index"
    ><code
      v-if="segment.code"
      class="rounded bg-black/5 px-1 font-mono dark:bg-white/10"
      >{{ segment.text }}</code
    ><template v-else>{{ segment.text }}</template></template
  >
</template>
