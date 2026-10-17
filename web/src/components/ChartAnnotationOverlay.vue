<script setup lang="ts">
/**
 * The annotations' drag handles, resize grips and inline text box: DOM elements over the
 * canvas-drawn labels, laid out by the same layoutAnnotations call the chart painted with.
 *
 * <p>They are a <b>sibling</b> of the plot's own container rather than children of it, so a
 * pointerdown on a handle never reaches uPlot's `over` element and a label drag can therefore never
 * start a drag-zoom — which is the whole reason the hit-testing is not done on the canvas. The
 * labels themselves stay canvas-drawn, because the report, the Screenshot dialog and the
 * black-and-white print only ever see a chart's exportPng canvas.</p>
 *
 * <p>One component for every annotated chart: it takes the controller
 * ({@link useAnnotationOverlay}) and nothing else, so adding annotations to a chart is one tag.</p>
 */
import { MAX_ANNOTATION_TEXT } from '../measurements/chartAnnotations'
import type { AnnotationController } from '../charts/useAnnotationOverlay'

const props = defineProps<{ controller: AnnotationController }>()

const c = props.controller
</script>

<template>
  <div class="pointer-events-none absolute" :style="c.overlayStyle.value">
    <div
      v-for="handle in c.labelHandles.value"
      :key="handle.id"
      class="pointer-events-auto absolute cursor-move rounded hover:bg-neutral-500/10"
      :style="handle.style"
      :title="handle.title"
      @pointerdown="c.onHandleDown(handle.id, $event)"
      @pointermove="c.onHandleMove"
      @pointerup="c.onHandleUp"
      @pointercancel="c.onHandleUp"
      @dblclick="c.beginEdit(handle.id)"
    ></div>
    <div
      v-for="grip in c.boxGrips.value"
      :key="grip.key"
      class="pointer-events-auto absolute rounded-sm hover:bg-neutral-500/20"
      :class="grip.cursor"
      :style="grip.style"
      :title="grip.title"
      @pointerdown="c.onBoxGripDown(grip.id, grip.kind, $event)"
      @pointermove="c.onBoxGripMove"
      @pointerup="c.onBoxGripUp"
      @pointercancel="c.onBoxGripUp"
    ></div>
    <input
      v-if="c.editing.value !== null"
      :ref="(el) => (c.editorRef.value = (el as HTMLInputElement | null))"
      v-model="c.editingText.value"
      type="text"
      :maxlength="MAX_ANNOTATION_TEXT"
      placeholder="What is this?"
      class="tb-input pointer-events-auto absolute h-7 text-xs"
      :style="c.editorStyle.value"
      @keydown.enter.prevent="c.commitEdit"
      @keydown.esc.prevent="c.cancelEdit"
      @blur="c.onEditorBlur"
    />
  </div>
</template>
