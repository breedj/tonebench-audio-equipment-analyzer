<script setup lang="ts">
import { useManual } from '../useManual'

const { showPage } = useManual()
</script>

<template>
  <div>
    <h1>Projects</h1>

    <p>
      A project is a file. Everything you do for one amplifier, one loudspeaker, one investigation —
      the measurements you set up, the results you keep, the notes you write — lives in a single
      <code>.tbench</code> file that you name and place yourself, the way a document does. Copy it,
      back it up, move it to another machine, keep it next to the schematic: the file is the project.
    </p>
    <p>
      Inside, it is an ordinary SQLite database. Two things deliberately do <em>not</em> live in it,
      because they describe this bench rather than the work: the impedance fixture calibration and
      the loopback calibrations of the impulse response. Those stay in the app's own configuration on
      this machine, so a project file opened elsewhere never quietly corrects that machine's
      measurements by this one's numbers.
    </p>

    <h2>The welcome screen</h2>
    <p>
      The app starts on a welcome screen while no project is open. It lists the ten project files you
      opened most recently, with the time each was last opened — click one to open it. A file that
      has since been moved, renamed or deleted is shown greyed out as <strong>file not found</strong>;
      the ✕ beside an entry removes it from the list and touches nothing on disk, which is your
      call to make, since a file on a drive that is not plugged in right now is not gone.
    </p>
    <p>
      <strong>New project…</strong> and <strong>Open project…</strong> do what the Project menu's
      items of the same name do, below. <strong>Manual</strong> opens this manual at this page.
    </p>
    <p>
      <strong>Show welcome page on startup</strong>, ticked by default, is the same setting the
      <button type="button" class="manual-link" @click="showPage('settings')">Configuration</button>
      dialog shows on its General tab. Untick it and the app opens the project that was open last
      time straight away — unless that file no longer exists, in which case it falls back to the
      welcome screen. The status bar and the Configuration dialog work on the welcome screen too, so
      the analyzer can be connected, or a simulator chosen, before any project is open.
    </p>

    <h2>The Project menu</h2>
    <p>
      The <strong>Project</strong> button at the top left opens a menu; when a project is open its
      name is shown beside the button, and the browser tab carries it too. Items that need an open
      project are greyed out while there is none.
    </p>
    <ul>
      <li>
        <strong>New…</strong> asks for a <strong>Name</strong>, an optional
        <strong>Description</strong> and a <strong>File location</strong>. The location follows the
        name — the app's projects folder plus the name as a file name — until you edit it or pick one
        with <strong>Browse…</strong>. The projects folder is <code>Documents\Tonebench Projects</code>
        on Windows and <code>~/Tonebench Projects</code> on Linux, created the first time a project is
        saved into it. <code>.tbench</code> is added when you leave it off. A name that is already
        taken in that folder is refused rather than overwritten.
      </li>
      <li>
        <strong>Open…</strong> opens the file dialog on the projects folder. The buttons along the top
        jump to Documents, your home folder and each drive; the ↑ goes up one folder; the path box
        takes a typed or pasted path — <kbd>Enter</kbd> lists a folder, or opens a file. Only
        <code>.tbench</code> files are listed. Double-click a file, or select it and press
        <strong>Open</strong>.
      </li>
      <li>
        <strong>Save</strong> writes every changed measurement in the project at once — it is
        enabled only when something is actually unsaved, and <kbd>Ctrl</kbd>+<kbd>S</kbd> does the
        same. Results are saved the moment you save them; this is about the measurements' settings
        and notes. See
        <button type="button" class="manual-link" @click="showPage('saving')">Projects, saving and history</button>.
      </li>
      <li>
        <strong>Save As…</strong> writes the project to a new file and continues in the copy; the
        original is left exactly as it was. The dialog starts with the current name and description
        and an empty location. <strong>Include results</strong>, ticked by default, copies the whole
        file, stored results and all; unticked, the new file holds the measurements and their
        settings only — a template for a second unit of the same design. Changed measurements are
        saved first, so what is copied is what is on screen.
      </li>
      <li>
        <strong>Properties…</strong> renames the project or edits its description. The name is what
        the welcome screen, the browser tab and a project report's title page show; the file's name
        does not change.
      </li>
      <li>
        <strong>Report…</strong> makes the PDF report of the whole project — see
        <button type="button" class="manual-link" @click="showPage('saving')">Projects, saving and history</button>.
      </li>
      <li>
        <strong>Delete</strong> closes the project and deletes its file, after a confirmation that
        names the file. Every measurement and result in it goes with it, and nothing goes to a
        recycle bin: this cannot be undone.
      </li>
      <li>
        <strong>Close</strong> closes the project and returns to the welcome screen.
      </li>
    </ul>
    <p>
      Opening, creating, closing and deleting all ask first when the open measurement has unsaved
      changes, and all stop a running measurement. While a sweep or an acquisition is running they
      are refused with a message — stop it, then try again.
    </p>

    <h2>What can be opened</h2>
    <p>
      A file made by <strong>New…</strong> or <strong>Save As…</strong>, on any machine. A file
      written by an older version of the app is brought up to date when it is opened, which means
      opening is a write: a read-only file, or one on a read-only medium, fails with a message saying
      so. A file that is not one of this app's databases is refused without being touched.
    </p>
    <p>
      Project files written before the app was renamed end in <code>.qa403</code> rather than
      <code>.tbench</code>. Both are listed by the Open dialog and both open normally: a project file
      is recognised by what is inside it, not by what it is called. Saving keeps a file's own
      extension, so an existing <code>.qa403</code> project stays where and what it is; use
      <strong>Save As…</strong> if you would rather have the new one.
    </p>
    <p>
      Before projects were one file each, every project lived together in one database,
      <code>qa403.db</code> under <code>%LOCALAPPDATA%\Qa403</code> (<code>~/.local/share/Qa403</code>
      on Linux — the folder the app used under its old name). That file is left where it is. When it
      holds a single project it opens like any other — type its path into the Open dialog's path box,
      since the list shows project files rather than databases — and from then on it is simply a
      project file with an old name. When it holds several, it is refused with a message naming them,
      because a project file holds one.
    </p>

    <h2>Where to keep the files</h2>
    <p>
      On a local disk. A project file is written in SQLite's write-ahead mode, which needs the shared
      memory that network file systems (SMB shares, NFS) do not provide reliably; a file kept on a
      share can be corrupted by an ordinary save. Work on a local copy and copy it to the share
      afterwards, or let a sync client do that. One app at a time per file, for the same reason.
    </p>
  </div>
</template>
