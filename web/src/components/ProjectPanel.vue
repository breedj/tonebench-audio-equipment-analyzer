<script setup lang="ts">
import { reactive } from 'vue'
import { useProjects } from '../composables/useProjects'

const { projects, currentProjectId, error, createProject, deleteProject, selectProject } = useProjects()

const newProject = reactive({ name: '', notes: '' })

async function onCreateProject() {
  if (!newProject.name.trim()) return
  const id = await createProject({ name: newProject.name.trim(), notes: newProject.notes.trim() || null })
  newProject.name = ''
  newProject.notes = ''
  await selectProject(id)
}

async function onDeleteProject(id: number) {
  await deleteProject(id)
}
</script>

<template>
  <section class="rounded-lg border border-neutral-200 p-4 dark:border-neutral-800">
    <h2 class="mb-3 text-sm font-semibold text-neutral-900 dark:text-neutral-100">Projects</h2>

    <p v-if="error" class="mb-2 text-xs text-red-600 dark:text-red-400">{{ error }}</p>

    <ul class="mb-3 flex flex-col gap-1 text-sm">
      <li
        v-for="p in projects"
        :key="p.id"
        class="flex items-center justify-between rounded px-2 py-1"
        :class="p.id === currentProjectId ? 'bg-neutral-100 dark:bg-neutral-800' : ''"
      >
        <button type="button" class="flex-1 text-left" @click="selectProject(p.id)">
          {{ p.name }}
          <span v-if="p.notes" class="text-neutral-400"> — {{ p.notes }}</span>
        </button>
        <button type="button" class="text-xs text-neutral-400 hover:text-red-600" @click="onDeleteProject(p.id)">delete</button>
      </li>
      <li v-if="projects.length === 0" class="text-xs text-neutral-400 dark:text-neutral-600">No projects yet.</li>
    </ul>

    <div class="flex flex-wrap items-end gap-2 text-sm">
      <input
        v-model="newProject.name"
        type="text"
        placeholder="New project name"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
      <input
        v-model="newProject.notes"
        type="text"
        placeholder="Notes (optional)"
        class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
      />
      <button
        type="button"
        class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
        @click="onCreateProject"
      >
        Add project
      </button>
    </div>
  </section>
</template>
