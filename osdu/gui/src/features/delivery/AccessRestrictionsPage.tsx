import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Lock, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";
import { useAuth } from "@/auth/AuthContext";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { EmptyState } from "@/components/EmptyState";
import { FilterBar } from "@/components/FilterBar";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { ACCESS_RESTRICTIONS_KEY, accessRestrictionsApi, type AccessRestriction } from "../../api/accessRestrictions";
import {
  groupsOf, groupsProblem, MAX_RESTRICTION_DESCRIPTION, MAX_RESTRICTION_NAME, nameProblem,
} from "./accessRestrictionText";
import { useActivePartition } from "./activePartition";
import { problemText } from "./problemText";
import { usePartitions } from "./usePartitions";

/** One field's problem under it, in the destructive tone, only once there is something to say. */
function FieldProblem({ problem, testId }: { problem: string | null; testId: string }) {
  return problem === null ? null : <p className="text-xs text-destructive" data-testid={testId}>{problem}</p>;
}

/** A list of groups, one a line, each in full: what a restricted record's ACL becomes. */
function Groups({ groups, testId }: { groups: readonly string[]; testId: string }) {
  return (
    <span className="flex min-w-0 flex-col" data-testid={testId}>
      {groups.map((group) => <span key={group} className="break-all font-mono text-[12px]">{group}</span>)}
    </span>
  );
}

/**
 * Registers an access restriction in a side sheet: its partition, its name, what it is for, and the groups it sets as viewers
 * and owners, typed one a line and checked as storage checks an ACL before anything is sent. The groups never change once
 * registered, so the sheet says so.
 */
function RegisterSheet({ partitions, initialPartition, onClose }: { partitions: string[]; initialPartition: string | null; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [partition, setPartition] = useState(initialPartition ?? partitions[0] ?? "");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [viewersText, setViewersText] = useState("");
  const [ownersText, setOwnersText] = useState("");
  const [sameOwners, setSameOwners] = useState(true);
  // Problems are said once the operator has had a go at a field, or asked to register.
  const [tried, setTried] = useState(false);

  const viewers = groupsOf(viewersText);
  const owners = sameOwners ? viewers : groupsOf(ownersText);
  const problems = {
    partition: partition === "" ? "Pick the partition whose groups these are." : null,
    name: nameProblem(name),
    description: description.trim().length > MAX_RESTRICTION_DESCRIPTION
      ? `At most ${MAX_RESTRICTION_DESCRIPTION} characters; this is ${description.trim().length}.`
      : null,
    viewers: groupsProblem(viewers, "viewers"),
    owners: sameOwners ? null : groupsProblem(owners, "owners"),
  };
  const valid = Object.values(problems).every((problem) => problem === null);

  const add = useMutation({
    mutationFn: () => accessRestrictionsApi.add({
      partition,
      name: name.trim(),
      description: description.trim() === "" ? null : description.trim(),
      viewers,
      owners,
    }),
    onSuccess: (added) => {
      toast.success(`${added.name} registered for ${added.partition}.`);
      void queryClient.invalidateQueries({ queryKey: ACCESS_RESTRICTIONS_KEY });
      onClose();
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const shown = (problem: string | null) => (tried ? problem : null);
  return (
    <Sheet open onOpenChange={(next) => { if (!next && !add.isPending) { onClose(); } }}>
      <SheetContent className="w-full sm:max-w-md" data-testid="restriction-register-sheet">
        <SheetHeader>
          <SheetTitle>Register an access restriction</SheetTitle>
          <SheetDescription>
            The groups a restricted record&apos;s ACL in OSDU becomes. They never change once registered: register another for
            other groups.
          </SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="restriction-partition">Partition</Label>
            {partitions.length === 1
              ? <span className="font-mono text-[13px]" data-testid="restriction-partition">{partitions[0]}</span>
              : (
                <Select value={partition} onValueChange={setPartition} disabled={add.isPending}>
                  <SelectTrigger id="restriction-partition" size="sm" className="h-8" data-testid="restriction-partition">
                    <SelectValue placeholder="Pick a partition" />
                  </SelectTrigger>
                  <SelectContent>
                    {partitions.map((one) => <SelectItem key={one} value={one}>{one}</SelectItem>)}
                  </SelectContent>
                </Select>
              )}
            <FieldProblem problem={shown(problems.partition)} testId="restriction-partition-problem" />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="restriction-name">Name</Label>
            <Input
              id="restriction-name"
              className="h-8"
              maxLength={MAX_RESTRICTION_NAME}
              placeholder="e.g. legal-hold"
              value={name}
              onChange={(event) => setName(event.target.value)}
              disabled={add.isPending}
              data-testid="restriction-name"
            />
            <FieldProblem problem={shown(problems.name)} testId="restriction-name-problem" />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="restriction-description">What it is for</Label>
            <Textarea
              id="restriction-description"
              className="min-h-14 text-[13px]"
              placeholder="e.g. Records under legal review: readable by the data office only"
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              disabled={add.isPending}
              data-testid="restriction-description"
            />
            <FieldProblem problem={problems.description} testId="restriction-description-problem" />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="restriction-viewers">Viewers</Label>
            <Textarea
              id="restriction-viewers"
              className="min-h-16 font-mono text-[12px]"
              placeholder={"data.legal-hold@opendes.contoso.com"}
              value={viewersText}
              onChange={(event) => setViewersText(event.target.value)}
              disabled={add.isPending}
              data-testid="restriction-viewers"
            />
            <span className="text-[11px] text-muted-foreground">One data group a line: who may still read a restricted record.</span>
            <FieldProblem problem={shown(problems.viewers)} testId="restriction-viewers-problem" />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label className="flex items-center gap-2 font-normal">
              <Checkbox checked={sameOwners} onCheckedChange={(checked) => setSameOwners(checked === true)} disabled={add.isPending} data-testid="restriction-same-owners" />
              Owners are the same groups
            </Label>
            {!sameOwners && (
              <>
                <Textarea
                  id="restriction-owners"
                  aria-label="Owners"
                  className="min-h-16 font-mono text-[12px]"
                  placeholder={"data.legal-hold.owners@opendes.contoso.com"}
                  value={ownersText}
                  onChange={(event) => setOwnersText(event.target.value)}
                  disabled={add.isPending}
                  data-testid="restriction-owners"
                />
                <FieldProblem problem={shown(problems.owners)} testId="restriction-owners-problem" />
              </>
            )}
            <span className="text-[11px] text-muted-foreground">
              Storage lets only an owner change the record again, so a flow restricts records only while its own identity is a
              member of an owner group: otherwise it could never lift the restriction. That is checked when records are restricted.
            </span>
          </div>
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={add.isPending}>Cancel</Button>
          <Button
            size="sm"
            onClick={() => {
              setTried(true);
              if (valid) {
                add.mutate();
              }
            }}
            disabled={add.isPending || (tried && !valid)}
            data-testid="restriction-register-save"
          >
            {add.isPending && <Loader2 className="animate-spin" />}
            Register
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

/** Says what a registered restriction is for, in a side sheet; its groups stay as registered. */
function DescribeSheet({ editing, onClose }: { editing: AccessRestriction; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [description, setDescription] = useState(editing.description ?? "");
  const save = useMutation({
    mutationFn: () => accessRestrictionsApi.describe(editing.restrictionId, description.trim() === "" ? null : description.trim()),
    onSuccess: (saved) => {
      toast.success(`${saved.name} described.`);
      void queryClient.invalidateQueries({ queryKey: ACCESS_RESTRICTIONS_KEY });
      onClose();
    },
    onError: (error) => toast.error(problemText(error)),
  });
  const tooLong = description.trim().length > MAX_RESTRICTION_DESCRIPTION;
  return (
    <Sheet open onOpenChange={(next) => { if (!next && !save.isPending) { onClose(); } }}>
      <SheetContent className="w-full sm:max-w-md" data-testid="restriction-describe-sheet">
        <SheetHeader>
          <SheetTitle>Describe {editing.name}</SheetTitle>
          <SheetDescription>What the restriction is for, as the removal dialog shows it. Its groups stay as registered.</SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-1.5 overflow-y-auto px-4">
          <Label htmlFor="restriction-describe">What it is for</Label>
          <Textarea
            id="restriction-describe"
            className="min-h-16 text-[13px]"
            value={description}
            onChange={(event) => setDescription(event.target.value)}
            disabled={save.isPending}
            data-testid="restriction-describe"
          />
          <FieldProblem
            problem={tooLong ? `At most ${MAX_RESTRICTION_DESCRIPTION} characters; this is ${description.trim().length}.` : null}
            testId="restriction-describe-problem"
          />
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={save.isPending}>Cancel</Button>
          <Button size="sm" onClick={() => save.mutate()} disabled={save.isPending || tooLong} data-testid="restriction-describe-save">
            {save.isPending && <Loader2 className="animate-spin" />}
            Save
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

/** Whether a restriction is found by what is typed: its name, what it is for, or one of its groups. */
function matches(restriction: AccessRestriction, typed: string): boolean {
  const term = typed.trim().toLowerCase();
  return term === ""
    || restriction.name.toLowerCase().includes(term)
    || (restriction.description ?? "").toLowerCase().includes(term)
    || [...restriction.viewers, ...restriction.owners].some((group) => group.toLowerCase().includes(term));
}

/**
 * The access restrictions registered for the workbench's partition (osdu/docs/reference/concepts/access-restriction.md, The
 * registry): each with what it is for, the groups a restricted record's ACL in OSDU becomes, and how many records carry it
 * now. A removal's Restrict access picks one; lifting a record's restriction gives it back the ACL it had. Reading is
 * anyone's; registering, describing and removing one is an admin's, and one records carry is not removed.
 */
export default function AccessRestrictionsPage() {
  const queryClient = useQueryClient();
  const { hasScope } = useAuth();
  const admin = hasScope("admin");
  const [active] = useActivePartition();
  const { list: partitionList } = usePartitions();
  const registeredPartitions = useMemo(() => partitionList.filter((p) => p.registered).map((p) => p.name), [partitionList]);
  const [search, setSearch] = useState("");
  const [registering, setRegistering] = useState(false);
  const [describing, setDescribing] = useState<AccessRestriction | null>(null);
  const [removing, setRemoving] = useState<AccessRestriction | null>(null);

  const restrictions = useQuery({
    queryKey: [...ACCESS_RESTRICTIONS_KEY, active],
    queryFn: () => accessRestrictionsApi.list(active),
  });
  const all = restrictions.data ?? [];
  const shown = all.filter((restriction) => matches(restriction, search));
  // Read in one partition, each row is that partition's; with none picked, every partition's, each naming its own.
  const columns = [
    "Restriction", ...(active === null ? ["Partition"] : []), "Viewers", "Owners", "Records", "Registered", ...(admin ? ["Actions"] : []),
  ];

  const remove = useMutation({
    mutationFn: (restriction: AccessRestriction) => accessRestrictionsApi.remove(restriction.restrictionId),
    onSuccess: (_result, restriction) => {
      toast.success(`${restriction.name} removed from the registry.`);
      void queryClient.invalidateQueries({ queryKey: ACCESS_RESTRICTIONS_KEY });
      setRemoving(null);
    },
    onError: (error) => {
      toast.error(problemText(error));
      setRemoving(null);
    },
  });

  return (
    <Page data-testid="page-delivery-access-restrictions">
      <PageHeader
        title="Access restrictions"
        subtitle={`The groups a record's ACL in OSDU can be restricted to${active === null ? "" : ` in ${active}`}, without removing it. A removal's Restrict access picks one; lifting gives each record back the access it had.`}
      />

      <FilterBar>
        <SearchInput value={search} onChange={setSearch} placeholder="Name, purpose or group" label="Search restrictions" testId="restrictions-search" className="sm:w-72" />
        {admin && (
          <Button
            variant="outline"
            size="sm"
            className="h-8"
            onClick={() => setRegistering(true)}
            disabled={registeredPartitions.length === 0}
            title={registeredPartitions.length === 0 ? "A restriction belongs to a registered partition, and none is registered." : undefined}
            data-testid="restrictions-register"
          >
            <Plus />
            Register
          </Button>
        )}
      </FilterBar>

      {restrictions.isError && <p className="text-[13px] text-destructive" data-testid="restrictions-error">{problemText(restrictions.error)}</p>}

      <Card className="gap-0 overflow-hidden rounded-lg p-0">
        <Table>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              {columns.map((header) => (
                <TableHead
                  key={header}
                  className={`h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground${header === "Records" || header === "Actions" ? " text-right" : ""}`}
                >
                  {header}
                </TableHead>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {restrictions.data === undefined && !restrictions.isError && Array.from({ length: 3 }, (_, i) => (
              <TableRow key={`skeleton-${i}`}>
                {columns.map((header) => <TableCell key={header} className="px-3 py-2"><Skeleton className="h-4 w-full" /></TableCell>)}
              </TableRow>
            ))}
            {restrictions.data !== undefined && shown.length === 0 && (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={columns.length} className="whitespace-normal border-0 p-0">
                  {all.length > 0
                    ? <EmptyState title="No restriction matches" description="Nothing registered here has that in its name, its purpose or its groups." />
                    : (
                      <EmptyState
                        icon={<Lock />}
                        title={`No access restriction is registered${active === null ? "" : ` for ${active}`}`}
                        description={(
                          <>
                            A restriction names the entitlements groups a record&apos;s ACL in OSDU is replaced with when a record should
                            stay but no longer be readable by its usual viewers, such as a legal hold. Create the group in Entitlements,
                            add the delivery&apos;s own identity to it so it can lift the restriction again, then register it here.
                          </>
                        )}
                        action={admin && registeredPartitions.length > 0
                          ? <Button size="sm" onClick={() => setRegistering(true)} data-testid="restrictions-register-empty"><Plus />Register one</Button>
                          : undefined}
                        data-testid="restrictions-empty"
                      />
                    )}
                </TableCell>
              </TableRow>
            )}
            {shown.map((restriction) => (
              <TableRow key={restriction.restrictionId} data-testid={`restriction-row-${restriction.name}`}>
                <TableCell className="min-w-36 max-w-64 whitespace-normal px-3 py-1.5">
                  <span className="flex min-w-0 flex-col">
                    <span className="text-[13px] font-medium">{restriction.name}</span>
                    {restriction.description !== null && <span className="text-[12px] text-muted-foreground">{restriction.description}</span>}
                  </span>
                </TableCell>
                {active === null && <TableCell className="whitespace-nowrap px-3 py-1.5 font-mono text-[13px]">{restriction.partition}</TableCell>}
                <TableCell className="min-w-40 max-w-64 whitespace-normal px-3 py-1.5"><Groups groups={restriction.viewers} testId={`restriction-viewers-${restriction.name}`} /></TableCell>
                <TableCell className="min-w-40 max-w-64 whitespace-normal px-3 py-1.5"><Groups groups={restriction.owners} testId={`restriction-owners-${restriction.name}`} /></TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-right font-mono text-[13px] tabular-nums" data-testid={`restriction-records-${restriction.name}`}>
                  <span className={restriction.records === 0 ? "text-muted-foreground" : undefined}>{restriction.records.toLocaleString()}</span>
                </TableCell>
                <TableCell className="max-w-40 whitespace-normal px-3 py-1.5 text-[13px]">
                  <span className="flex min-w-0 flex-col" title={`by ${restriction.createdBy}, last changed by ${restriction.updatedBy}`}>
                    <RelativeTime value={restriction.createdUtc} absolute={false} />
                    <span className="truncate text-[12px] text-muted-foreground">by {restriction.createdBy}</span>
                  </span>
                </TableCell>
                {admin && (
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-right">
                    <span className="inline-flex gap-1">
                      <Button variant="ghost" size="xs" onClick={() => setDescribing(restriction)} data-testid={`restriction-describe-${restriction.name}`}>
                        Describe
                      </Button>
                      <Button
                        variant="ghost"
                        size="xs"
                        className="text-destructive hover:text-destructive"
                        disabled={restriction.records > 0}
                        title={restriction.records > 0
                          ? `${restriction.records.toLocaleString()} record${restriction.records === 1 ? " carries" : "s carry"} it: lift their restrictions first (a flow's Records tab, Restricted, then Remove).`
                          : undefined}
                        onClick={() => setRemoving(restriction)}
                        data-testid={`restriction-remove-${restriction.name}`}
                      >
                        Remove
                      </Button>
                    </span>
                  </TableCell>
                )}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>

      {registering && (
        <RegisterSheet
          partitions={registeredPartitions}
          initialPartition={active !== null && registeredPartitions.includes(active) ? active : null}
          onClose={() => setRegistering(false)}
        />
      )}
      {describing !== null && <DescribeSheet editing={describing} onClose={() => setDescribing(null)} />}
      <ConfirmDialog
        open={removing !== null}
        title={`Remove ${removing?.name ?? ""} from the registry?`}
        message="No record carries it, so nothing in OSDU changes. A removal can no longer restrict records under it."
        confirmLabel="Remove"
        danger
        busy={remove.isPending}
        onConfirm={() => { if (removing !== null) { remove.mutate(removing); } }}
        onClose={() => setRemoving(null)}
      />
    </Page>
  );
}
