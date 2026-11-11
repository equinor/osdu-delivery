# Onboarding

The videos in parts, in the order to watch them. The first part gives the whole picture; the next three follow a record
the way it travels, from the source files into the ingestion tables, from a row to an OSDU record, and into OSDU; then
come checking what OSDU holds after delivery, and building on it. Each video is tagged for whom it serves:
<img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> for building
pipelines with OSDU Delivery, <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending">
for changing the framework itself. Most serve both.

Short on time: watch Start here, then the mapping tutorial to write a mapping or the codebase walkthrough to change the
code. Every video is on the [deltaforge-org YouTube channel](https://www.youtube.com/@deltaforge-org).

## Start here

Why OSDU Delivery exists, how its parts fit together, and what runs it.

<table>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=yffQyPOpJcA"><img src="https://img.youtube.com/vi/yffQyPOpJcA/maxresdefault.jpg" width="100%" alt="Stop building OSDU ingestion pipelines"></a></td>
<td width="62%" valign="top">
<h3>01 <a href="https://www.youtube.com/watch?v=yffQyPOpJcA">Stop building OSDU ingestion pipelines</a></h3>
<p>3:42 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>Why OSDU Delivery exists: one custom loader per data type, replaced by configuration.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=vxYiGkvaSLs"><img src="https://img.youtube.com/vi/vxYiGkvaSLs/maxresdefault.jpg" width="100%" alt="How OSDU Delivery works"></a></td>
<td width="62%" valign="top">
<h3>02 <a href="https://www.youtube.com/watch?v=vxYiGkvaSLs">How OSDU Delivery works</a></h3>
<p>6:18 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>The engine and the module, what runs where, and one record through three flows. No coding or OSDU knowledge
needed.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=-KRiXBwmPto"><img src="https://img.youtube.com/vi/-KRiXBwmPto/maxresdefault.jpg" width="100%" alt="What actually runs OSDU Delivery? Three engines explained"></a></td>
<td width="62%" valign="top">
<h3>03 <a href="https://www.youtube.com/watch?v=-KRiXBwmPto">What actually runs OSDU Delivery?</a></h3>
<p>4:41 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>Three engines, one job each: SQLFlow runs the flows, SQL Server does the heavy lifting where the data lives, and the
mapping engine reads a mapping once and renders every row alike, touching only memory. What a run holds does not grow
with the table.</p>
</td>
</tr>
</table>

## Into the ingestion tables

Which flow does what, then how the source files land and their rows are keyed: only what changed is written, a new
column arrives on its own, and a changed key goes to the table before the flow.

<table>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=JVtJQ15Cipg"><img src="https://img.youtube.com/vi/JVtJQ15Cipg/maxresdefault.jpg" width="100%" alt="SQLFlow's 14 flow types explained: choosing the right data pipeline"></a></td>
<td width="62%" valign="top">
<h3>04 <a href="https://www.youtube.com/watch?v=JVtJQ15Cipg">SQLFlow's 14 flow types</a></h3>
<p>6:34 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>A flow is one YAML file, and its <code>flowType</code> says which of fourteen it is. Two questions choose one, where
the data is now and where it must go: API, SFTP and Copy move data, File and Ingestion load tables, Export and Translate
deliver it, and Batch runs flows in lineage order.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=uha2OVcFOBk"><img src="https://img.youtube.com/vi/uha2OVcFOBk/maxresdefault.jpg" width="100%" alt="How SQLFlow knows what changed: incremental loading and backfills"></a></td>
<td width="62%" valign="top">
<h3>05 <a href="https://www.youtube.com/watch?v=uha2OVcFOBk">How SQLFlow knows what changed</a></h3>
<p>7:12 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>The source sends everything and almost nothing changed: the table itself remembers how far a flow got, file and row
fingerprints decide what is new, and a backfill is just another run.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=Q-5sXYmtnEw"><img src="https://img.youtube.com/vi/Q-5sXYmtnEw/maxresdefault.jpg" width="100%" alt="Schema evolution without breaking data pipelines"></a></td>
<td width="62%" valign="top">
<h3>06 <a href="https://www.youtube.com/watch?v=Q-5sXYmtnEw">Schema evolution without breaking pipelines</a></h3>
<p>9:43 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"></p>
<p>A new source column flows through the landing and ingestion tables on its own, and an OSDU document changes only
once the mapping takes it up. Columns are added and widened, never dropped or narrowed, and a costly rewrite waits for
approval.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=9ooMnsMf48A"><img src="https://img.youtube.com/vi/9ooMnsMf48A/maxresdefault.jpg" width="100%" alt="Why changing a SQLFlow key breaks your load, and how to fix it"></a></td>
<td width="62%" valign="top">
<h3>07 <a href="https://www.youtube.com/watch?v=9ooMnsMf48A">Why changing a key breaks your load</a></h3>
<p>7:19 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"></p>
<p>A key lives in two places: the flow, read on every run, and the unique index the table was built with. Change the
flow alone and inserts fail; the rule is the table first, then the flow, with a key type SQL Server can index, or a
hash key when the source has none.</p>
</td>
</tr>
</table>

## From row to record

The flows OSDU Delivery adds and which to choose, then the mapping that renders an OSDU record from a row, a kind of
your own when OSDU's do not fit, the cache and the joined lookup tables that answer its references, and when a change
sends it again.

<table>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=cE0Br5uJRgo"><img src="https://img.youtube.com/vi/cE0Br5uJRgo/maxresdefault.jpg" width="100%" alt="8 OSDU Delivery flow types explained: which one do you need?"></a></td>
<td width="62%" valign="top">
<h3>08 <a href="https://www.youtube.com/watch?v=cE0Br5uJRgo">OSDU Delivery's 8 flow types</a></h3>
<p>6:42 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>The flows OSDU Delivery adds to SQLFlow, through one well log: delivery, cache, removal, retrieval, download,
assertion, dimension and inventory. Which way the arrow points, and what is on the other end, picks one; then which
of them change OSDU, how to preview without changing it, and how lineage orders them.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=k8XU0Boqh4w"><img src="https://img.youtube.com/vi/k8XU0Boqh4w/maxresdefault.jpg" width="100%" alt="Map curated data to OSDU with YAML"></a></td>
<td width="62%" valign="top">
<h3>09 <a href="https://www.youtube.com/watch?v=k8XU0Boqh4w">Map curated data to OSDU with YAML</a></h3>
<p>17:30 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"></p>
<p>A hands-on mapping tutorial: where each value comes from, references, computed values, rules stated with
<code>$assert</code>, and records held on purpose.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=dJUD0bhTu6c"><img src="https://img.youtube.com/vi/dJUD0bhTu6c/maxresdefault.jpg" width="100%" alt="OSDU custom schemas: what happens when your data doesn't fit?"></a></td>
<td width="62%" valign="top">
<h3>10 <a href="https://www.youtube.com/watch?v=dJUD0bhTu6c">Custom schemas: when your data doesn't fit</a></h3>
<p>7:53 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"></p>
<p>When a record needs fields OSDU's kinds do not have: a kind of your own, registered in the partition before anything
is delivered to it, kept in Git and in OSDU, with <code>sqlflow template register</code> finding what is missing or has
drifted. A custom companion sits beside the standard WellLog and is linked to it, with no curve data copied.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=ar-cwYginZo"><img src="https://img.youtube.com/vi/ar-cwYginZo/maxresdefault.jpg" width="100%" alt="OSDU Delivery caching explained: ask once, render many"></a></td>
<td width="62%" valign="top">
<h3>11 <a href="https://www.youtube.com/watch?v=ar-cwYginZo">Ask once, render many</a></h3>
<p>7:08 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>One well log can need more than 40 references, each answered by a versioned cache per partition rather than by
OSDU: how the cache is filled and kept current with <code>cache.before</code> and <code>cache.after</code>, and how a
held record is released once its missing reference appears.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=_yBx6uFpErA"><img src="https://img.youtube.com/vi/_yBx6uFpErA/maxresdefault.jpg" width="100%" alt="OSDU Delivery: why we stopped caching huge lookup tables"></a></td>
<td width="62%" valign="top">
<h3>12 <a href="https://www.youtube.com/watch?v=_yBx6uFpErA">Why we stopped caching huge lookup tables</a></h3>
<p>6:17 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>A lookup table too large for every worker's cache stays in SQL Server: joined into the delivery's read, its columns
reach the mapping, and a changed lookup row sends the records it touched again. When the cache still fits, and when a
join is the better choice.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=t5d1s_9fBGw"><img src="https://img.youtube.com/vi/t5d1s_9fBGw/maxresdefault.jpg" width="100%" alt="Change detection: will it send?"></a></td>
<td width="62%" valign="top">
<h3>13 <a href="https://www.youtube.com/watch?v=t5d1s_9fBGw">Will it send?</a></h3>
<p>9:52 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>Change detection in eighteen cases: which source, mapping and cache changes deliver again, which never do, and what
stops a duplicate when runs overlap.</p>
</td>
</tr>
</table>

## Into OSDU

The routes into OSDU and the files that travel them, however late or large, the ledger that accounts for every record
sent, and the workers that share the load.

<table>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=rDVmUhtHHVU"><img src="https://img.youtube.com/vi/rDVmUhtHHVU/maxresdefault.jpg" width="100%" alt="OSDU data ingestion: 5 doors, 10 routes and silent failures"></a></td>
<td width="62%" valign="top">
<h3>14 <a href="https://www.youtube.com/watch?v=rDVmUhtHHVU">5 doors, 10 routes and silent failures</a></h3>
<p>16:15 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>One well log is a record, a file and curve data, and OSDU takes each through its own service: the routes in, and how
a delivery fails while every call answers success.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=UweClTQ8tJA"><img src="https://img.youtube.com/vi/UweClTQ8tJA/maxresdefault.jpg" width="100%" alt="How to add a LAS file to an existing OSDU well log"></a></td>
<td width="62%" valign="top">
<h3>15 <a href="https://www.youtube.com/watch?v=UweClTQ8tJA">Add a LAS file to an existing well log</a></h3>
<p>4:28 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"></p>
<p>A well log can go in before its LAS file: enrichment adds the file to the delivered log through the Dataset service,
under an id that stays the same, without sending the curves again. A corrected file updates the dataset under that
id, so the log needs no new version.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=2sxnXJE8HCA"><img src="https://img.youtube.com/vi/2sxnXJE8HCA/maxresdefault.jpg" width="100%" alt="How do you get a 16 GB file into OSDU?"></a></td>
<td width="62%" valign="top">
<h3>16 <a href="https://www.youtube.com/watch?v=2sxnXJE8HCA">How do you get a 16 GB file into OSDU?</a></h3>
<p>6:53 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>OSDU hands out a signed link and leaves the bytes to the sender: a large file goes to Azure in blocks and one commit,
streamed rather than held, copied between storage accounts when the store may read it, and retried block by block.
Only Azure takes blocks; other stores take a file in one request.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=qBJIg7qGZA8"><img src="https://img.youtube.com/vi/qBJIg7qGZA8/maxresdefault.jpg" width="100%" alt="8 ledger states explained"></a></td>
<td width="62%" valign="top">
<h3>17 <a href="https://www.youtube.com/watch?v=qBJIg7qGZA8">Eight ledger states</a></h3>
<p>13:13 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>One well log through its first month: the states a record moves through, held records and their release, reversing
a run, and removing records without a mass deletion.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=06ZcsDXt2O0"><img src="https://img.youtube.com/vi/06ZcsDXt2O0/maxresdefault.jpg" width="100%" alt="Scaling: why 5,000 records ran on one worker"></a></td>
<td width="62%" valign="top">
<h3>18 <a href="https://www.youtube.com/watch?v=06ZcsDXt2O0">Why 5,000 records ran on one worker</a></h3>
<p>9:52 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>Autoscalers count work, not records: a delivery split into parts, the rate limit shared among them, recovery when a
worker fails, and a scale-down that lets running work finish.</p>
</td>
</tr>
</table>

## After delivery

What OSDU really holds beside the ledger, and proof that what it holds is right.

<table>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=8a8YU9PFSwE"><img src="https://img.youtube.com/vi/8a8YU9PFSwE/maxresdefault.jpg" width="100%" alt="What's really in OSDU? Finding the hidden differences"></a></td>
<td width="62%" valign="top">
<h3>19 <a href="https://www.youtube.com/watch?v=8a8YU9PFSwE">What's really in OSDU?</a></h3>
<p>3:36 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"></p>
<p>The ledger knows what was sent, not what OSDU holds: an inventory of the partition finds orphans, missing records
and duplicates, keeps the most complete copy, and removes the rest with a soft delete that can be restored.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=fKtOs3eIEyU"><img src="https://img.youtube.com/vi/fKtOs3eIEyU/maxresdefault.jpg" width="100%" alt="OSDU assertions: can you prove your data is right?"></a></td>
<td width="62%" valign="top">
<h3>20 <a href="https://www.youtube.com/watch?v=fKtOs3eIEyU">Can you prove your data is right?</a></h3>
<p>7:41 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"></p>
<p>A successful delivery is not proof: assertion flows check what users see in OSDU against values known from the
source, account for every source row, held and failed ones included, and turn the result into dashboards, reports and
JUnit for CI.</p>
</td>
</tr>
</table>

## Build and extend

Lay out the repository an estate lives in, let an assistant write its configuration and see where the EU AI Act places
it, run a merged change straight from GitHub, and find where a change to the framework belongs.

<table>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=91fI1XZquGA"><img src="https://img.youtube.com/vi/91fI1XZquGA/maxresdefault.jpg" width="100%" alt="How to structure an OSDU Delivery estate repository: Git, flows and schedules"></a></td>
<td width="62%" valign="top">
<h3>21 <a href="https://www.youtube.com/watch?v=91fI1XZquGA">Structure an estate repository</a></h3>
<p>8:52 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"></p>
<p>One Git repository holds an estate's instructions, never its data: a folder per record kind with its flows, mappings
and templates, names that read in the order a record travels, two homes for the caches, one file of schedules, schema
versions side by side, every change through Git, and the common pitfalls: renaming a flow, which ledger owns a record,
and duplicate record ids.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=9jOs8EDwBk4"><img src="https://img.youtube.com/vi/9jOs8EDwBk4/maxresdefault.jpg" width="100%" alt="The code doesn't change: AI-driven data pipelines"></a></td>
<td width="62%" valign="top">
<h3>22 <a href="https://www.youtube.com/watch?v=9jOs8EDwBk4">The code doesn't change</a></h3>
<p>8:09 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"></p>
<p>Only the configuration does: an assistant reads the schema, drafts the mapping and opens a pull request, and a
person reviews it.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=-JwDR8WOWoQ"><img src="https://img.youtube.com/vi/-JwDR8WOWoQ/maxresdefault.jpg" width="100%" alt="EU AI Act explained: is OSDU Delivery high-risk AI?"></a></td>
<td width="62%" valign="top">
<h3>23 <a href="https://www.youtube.com/watch?v=-JwDR8WOWoQ">Is OSDU Delivery high-risk AI?</a></h3>
<p>7:38 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"> <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p>The EU AI Act read against OSDU Delivery: rules, not a model, move the data; the assistant reads but never changes
records, mappings or runs; and disclosure and human oversight keep people in charge. An interpretation, not legal
advice.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=40okusVYQZA"><img src="https://img.youtube.com/vi/40okusVYQZA/maxresdefault.jpg" width="100%" alt="OSDU external triggers: merge, run, get the result"></a></td>
<td width="62%" valign="top">
<h3>24 <a href="https://www.youtube.com/watch?v=40okusVYQZA">Merge, run, get the result</a></h3>
<p>3:38 · <img src="https://img.shields.io/badge/data_engineer-f2b56b?style=flat-square" alt="data engineer"></p>
<p>A merge in GitHub starts a delivery without waiting for its schedule: GitHub Actions signs in with OIDC and no stored
secret, the control plane runs the commit just merged, a repeated request runs once, and the result comes back to the
workflow.</p>
</td>
</tr>
<tr>
<td width="38%" valign="top"><a href="https://www.youtube.com/watch?v=QnOzD6ycfV8"><img src="https://img.youtube.com/vi/QnOzD6ycfV8/maxresdefault.jpg" width="100%" alt="Codebase walkthrough: architecture and where to make changes"></a></td>
<td width="62%" valign="top">
<h3>25 <a href="https://www.youtube.com/watch?v=QnOzD6ycfV8">Codebase walkthrough</a></h3>
<p>12:58 · <img src="https://img.shields.io/badge/extending-9cc8ff?style=flat-square" alt="extending"></p>
<p><code>osdu/</code> and <code>sqlflow/</code> and the line between them, one CSV row through the real classes, the
ledger's rules, and where a change belongs.</p>
</td>
</tr>
</table>
