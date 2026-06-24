using System;


// A grab-bag of beast-themed "launching" messages shown on the worktree screen while the sandbox
// container spins up. Purely cosmetic — one is picked at random each launch (see SelectMenu.Choose).
public static class LaunchNotes
{
	private static readonly string[] Notes = new string[]
	{
		"Housebreaking the sandbox...",
		"Evicting stray cats from the sandbox...",
		"Herding feral containers...",
		"Brewing coffee for the beast...",
		"Tossing a steak to the runtime...",
		"Letting the beast sniff your prompt...",
		"Bribing the container with extra RAM...",
		"Reticulating container splines...",
		"Applying thermal paste to the dragon...",
		"Explaining SOLID principles to the beast...",
		"Clearing execution with the beast's union rep...",
		"Apologizing to the beast for last time...",
		"Feeding the gremlins (strictly before midnight)...",
		"Lying to the container about its lifespan...",
		"Assuring the daemon it won't be terminated... probably...",
		"Waking the Kraken (gently)...",
		"Scanning the containers for rabies...",
		"Teaching the daemon to fetch...",
		"Pinging the beast on localhost...",
		"Untangling the tentacles...",
		"Polishing the beast's scales...",
		"Filing down the fangs...",
		"Convincing the environment to cooperate...",
		"Poking the background process with a stick...",
		"Counting the beast's teeth... wait, one is missing...",
		"Loading the chimera into the arena...",
		"Stitching the monster together...",
		"Tucking in the zombie processes...",
		"Deworming the daemons...",
		"Registering the runtime at the pound...",
		"Lassoing a runaway process...",
		"Lowering the drawbridge...",
		"Lighting the torches...",
		"Reminding the beast not to bite the user...",
		"Negotiating hostage release with the daemon...",
		"Injecting espresso directly into the sandbox...",
		"Spawning the creature...",
		"Conjuring the backend...",
		"Summoning ancient processes...",
		"Checking for monsters under the server rack...",
		"Brushing the beast's fur against the grain...",
		"Taking the beast for a walk around the filesystem...",
		"Calibrating the beast's growl...",
		"Microchipping the daemon...",
		"Refilling the kibble buffer...",
		"Scolding misbehaving containers...",
		"Releasing the hounds...",
		"Loosening the chains...",
		"Preening the griffin's feathers...",
		"Coaxing the dragon from its hoard...",
		"Buckling the beast's harness...",
		"Promising the beast a treat if it compiles...",
		"Walking the beast through the worktree..."
	};

	// Returns a randomly chosen launching note.
	public static string Pick()
	{
		return Notes[Random.Shared.Next(Notes.Length)];
	}
}