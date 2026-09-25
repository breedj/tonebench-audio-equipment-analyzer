<script setup lang="ts">
import { computed, ref } from 'vue'
import { GLOSSARY, groupGlossary, type GlossaryEntry } from '../glossary'
import { manualPageById } from '../pages'
import { useManual } from '../useManual'

// The listing, the search and the A-Z strip are all driven by groupGlossary(), so this component
// holds no logic of its own worth testing — which is the point, since nothing in a .vue file is
// reachable from the test run. See glossary.ts and glossary.test.ts.

const { showPage } = useManual()

const query = ref('')
const groups = computed(() => groupGlossary(query.value))

/** Every letter in the glossary, with the ones the current search has emptied greyed out. */
const letters = computed(() => {
  const present = new Set(groups.value.map((g) => g.letter))
  const all = new Set(GLOSSARY.map((e) => e.term.charAt(0).toUpperCase()))
  return [...all].sort().map((letter) => ({ letter, present: present.has(letter) }))
})

function headingId(letter: string) {
  return `glossary-letter-${letter}`
}

function jumpTo(letter: string) {
  document.getElementById(headingId(letter))?.scrollIntoView({ block: 'start', behavior: 'smooth' })
}

function seeAlso(entry: GlossaryEntry) {
  return (entry.see ?? []).map((id) => manualPageById(id)).filter((p) => p !== null)
}
</script>

<template>
  <div>
    <h1>Glossary</h1>

    <p>
      The terms the screens, the panels and the rest of this manual use, in one place. Definitions
      are written for this analyzer: where a term has more than one convention behind it — which
      level a distortion figure is divided by, which reference a bandwidth corner is measured down
      from — the entry says which one you are looking at, since that is usually the difference
      between two numbers that disagree.
    </p>

    <div class="glossary-search">
      <input
        v-model="query"
        type="search"
        class="glossary-search-input"
        placeholder="Search terms and definitions…"
        aria-label="Search the glossary"
      />
      <button v-if="query" type="button" class="manual-link" @click="query = ''">Clear</button>
    </div>

    <p v-if="!query" class="glossary-letters">
      <button
        v-for="l in letters"
        :key="l.letter"
        type="button"
        class="glossary-letter-jump"
        :disabled="!l.present"
        @click="jumpTo(l.letter)"
      >
        {{ l.letter }}
      </button>
    </p>

    <p v-if="groups.length === 0">
      Nothing here matches <strong>{{ query }}</strong
      >. Every word you type has to appear in an entry, so try one word rather than a phrase.
    </p>

    <template v-for="group in groups" :key="group.letter">
      <h2 :id="headingId(group.letter)">{{ group.letter }}</h2>
      <dl class="glossary-list">
        <template v-for="entry in group.entries" :key="entry.term">
          <dt>{{ entry.term }}</dt>
          <dd>
            <p>{{ entry.definition }}</p>
            <p v-if="entry.formula"><code>{{ entry.formula }}</code></p>
            <p v-if="seeAlso(entry).length" class="glossary-see">
              See
              <template v-for="(page, i) in seeAlso(entry)" :key="page.id">
                <span v-if="i > 0">, </span>
                <button type="button" class="manual-link" @click="showPage(page.id)">{{ page.title }}</button>
              </template>
            </p>
          </dd>
        </template>
      </dl>
    </template>
  </div>
</template>

<style>
/* Unscoped for the same reason ManualDialog's prose styles are: this page's markup is prose the
   dialog's own rules already reach, and these are the few shapes it does not have — a definition
   list, the search row and the A-Z strip. */
.manual-prose .glossary-search {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.6rem;
}
.manual-prose .glossary-search-input {
  flex: 1 1 auto;
  max-width: 22rem;
  border: 1px solid rgb(212 212 212);
  border-radius: 0.25rem;
  padding: 0.25rem 0.5rem;
  font-size: 0.8125rem;
  background: transparent;
  color: inherit;
}
.dark .manual-prose .glossary-search-input {
  border-color: rgb(64 64 64);
}
.manual-prose .glossary-letters {
  display: flex;
  flex-wrap: wrap;
  gap: 0.15rem;
}
.manual-prose .glossary-letter-jump {
  min-width: 1.3rem;
  border-radius: 0.25rem;
  padding: 0.05rem 0.25rem;
  font-size: 0.75rem;
  font-weight: 600;
  color: rgb(29 78 216);
}
.dark .manual-prose .glossary-letter-jump {
  color: rgb(147 197 253);
}
.manual-prose .glossary-letter-jump:hover:not(:disabled) {
  background: rgb(245 245 245);
}
.dark .manual-prose .glossary-letter-jump:hover:not(:disabled) {
  background: rgb(38 38 38);
}
.manual-prose .glossary-letter-jump:disabled {
  color: rgb(163 163 163);
}
.dark .manual-prose .glossary-letter-jump:disabled {
  color: rgb(82 82 82);
}
.manual-prose .glossary-list {
  margin-bottom: 0.75rem;
}
.manual-prose .glossary-list dt {
  font-weight: 600;
  color: rgb(23 23 23);
  margin-top: 0.7rem;
}
.dark .manual-prose .glossary-list dt {
  color: rgb(245 245 245);
}
.manual-prose .glossary-list dd {
  margin-left: 0;
  padding-left: 0.75rem;
  border-left: 2px solid rgb(229 229 229);
}
.dark .manual-prose .glossary-list dd {
  border-left-color: rgb(64 64 64);
}
.manual-prose .glossary-list dd p {
  margin-bottom: 0.3rem;
}
.manual-prose .glossary-see {
  font-size: 0.8125rem;
  color: rgb(115 115 115);
}
.dark .manual-prose .glossary-see {
  color: rgb(163 163 163);
}
</style>
