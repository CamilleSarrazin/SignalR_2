using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using signalr.backend.Data;
using signalr.backend.Models;

namespace signalr.backend.Hubs
{
    // On garde en mémoire les connexions actives (clé: email, valeur: userId)
    // Note: Ce n'est pas nécessaire dans le TP
    public static class UserHandler
    {
        public static Dictionary<string, string> UserConnections { get; set; } = new Dictionary<string, string>();
    }

    // L'annotation Authorize fonctionne de la même façon avec SignalR qu'avec Web API
    [Authorize]
    // Le Hub est le type de base des "contrôleurs" de SignalR
    public class ChatHub : Hub
    {
        public ApplicationDbContext _context;

        public IdentityUser CurentUser
        {
            get
            {
                // On récupère le userid à partir du Cookie qui devrait être envoyé automatiquement
                string userid = Context.UserIdentifier!;
                return _context.Users.Single(u => u.Id == userid);
            }
        }

        public ChatHub(ApplicationDbContext context)
        {
            _context = context;
        }

        public async override Task OnConnectedAsync()
        {
            // TODO: Envoyer des message aux clients pour les mettre à jour
            //on appelle donc notre méthode JoinChat qui permet d'avoir la liste des users connectés et la liste des channels
            await JoinChat();
        }

        //On veut créer une nouvelle méthode pour mettre la liste des users connectés à jour mais aussi la liste des canaux à la connexion
        public async Task JoinChat()
        {
            //On reprend ce qu'il y avait dans OnConnectedAsync
            UserHandler.UserConnections.Add(CurentUser.Email!, Context.UserIdentifier);

            //on va chercher la liste des users connectés avec notre méthode
            await UserList();

            //on veut envoyer au client connecté la liste des canaux (il faudra ajouter l'écoute sur le client pour ChannelsList)
            await Clients.Caller.SendAsync("ChannelsList", _context.Channel.ToList());

        }

        //Pour avoir la liste des users connectés (en lien avec le hub dans Angular, on écoute pour "UsersList"
        public async Task UserList()
        {
            // TODO On envoie un évènement de type UsersList à tous les Utilisateurs
            // TODO On peut envoyer en paramètre tous les types que l'om veut,
            // ici UserHandler.UserConnections.Keys correspond à la liste de tous les emails des utilisateurs connectés

            await Clients.All.SendAsync("UsersList", UserHandler.UserConnections.ToList());
        }

        public async override Task OnDisconnectedAsync(Exception? exception)
        {
            // Lors de la fermeture de la connexion, on met à jour notre dictionnary d'utilisateurs connectés
            KeyValuePair<string, string> entrie = UserHandler.UserConnections.SingleOrDefault(uc => uc.Value == Context.UserIdentifier);
            UserHandler.UserConnections.Remove(entrie.Key);

            // TODO: Envoyer un message aux clients pour les mettre à jour
            //on rappellera notre méthode UserList pour avoir la liste des users connectés
            await UserList();
        }

        public async Task CreateChannel(string title)
        {
            _context.Channel.Add(new Channel { Title = title });
            await _context.SaveChangesAsync();

            // TODO: Envoyer un message aux clients pour les mettre à jour
            //on veut aussi avoir la liste à jour des channels
            await Clients.All.SendAsync("ChannelsList", _context.Channel.ToList());
        }

        public async Task DeleteChannel(int channelId)
        {
            Channel channel = _context.Channel.Find(channelId);

            if(channel != null)
            {
                _context.Channel.Remove(channel);
                await _context.SaveChangesAsync();
            }
            string groupName = CreateChannelGroupName(channelId);
            // Envoyer les messages nécessaires aux clients
            //pour le groupe du channel, on veut commencer par dire que ce dernier a été supprimé
            await Clients.Group(groupName).SendAsync("NewMessage", "[" + channel.Title + "] a été détruit.");

            //on veut ensuite envoyer le message que les users de ce groupe on quitté ce channel
            await Clients.Group(groupName).SendAsync("LeaveChannel");

            //on veut ré envoyer la liste des channels aux users connectés
            await Clients.All.SendAsync("ChannelsList", await _context.Channel.ToListAsync());
        }

        public async Task JoinChannel(int oldChannelId, int newChannelId)
        {
            string userTag = "[" + CurentUser.Email! + "]";

            // TODO: Faire quitter le vieux canal à l'utilisateur
            if(oldChannelId > 0) ///Logique du > 0 ??? 
            {
                //on crée un string qui contient le nom de l'ancien groupe
                string oldGroupName = CreateChannelGroupName(oldChannelId);

                //on trouve le channel qui correspond au oldChannelId
                Channel channel = _context.Channel.Find(oldChannelId);

                //on crée un string qui va contenir le message qu'on veut envoyer aux gens du groupe
                string message = userTag + " a quitté: " + channel.Title;

                //on envoie le message aux gens du groupe que l'utilisateur un tel a quitté
                await Clients.Group(oldGroupName).SendAsync("NewMessage", message);

                //on retire le user du groupe par son connectionId
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, oldGroupName);
            }

            // TODO: Faire joindre le nouveau canal à l'utilisateur
            if(newChannelId > 0)
            {
                //on crée un string qui contient le nom du nouveau groupe
                string newGroupName = CreateChannelGroupName(newChannelId);

                //on ajoute le user au nouveau groupe
                await Groups.AddToGroupAsync(Context.ConnectionId, newGroupName);

                //on trouve le channel qui correspond au newChannel
                Channel channel = _context.Channel.Find(newChannelId);

                //on crée un string qui contient le message à afficher
                string message = userTag + " a rejoint: " + channel.Title;

                //On envoie le message aux gens du groupe que le user a rejoint le groupe
                await Clients.Group(newGroupName).SendAsync("NewMessage", message);
                
            }
        }

        public async Task SendMessage(string message, int channelId, string userId)
        {
            if (userId != null)
            {
                // TODO: Envoyer le message à cet utilisateur
                //Si on précise un user à qui envoyer un message, on l'envoie seulement a lui

                //on demande que le message contienne un tag (commence par le email du sender), on va donc stocker cela dans un string
                string messageWithTag = "[De: " + CurentUser.Email! + "] " + message;

                //on envoie le message seulement au user précisé avec notre message
                await Clients.User(userId).SendAsync("NewMessage", messageWithTag);
            }
            else if (channelId != 0)
            {
                // TODO: Envoyer le message aux utilisateurs connectés à ce canal
                //si on ne précise pas de user mais qu'on précise un groupe, on envoie le message à tous les membres du groupe (doit commencer par le nom du groupe)

                //on doit d'abord créer un string pour stocker le nom du groupe du channel précisé
                string groupName = CreateChannelGroupName(channelId);

                //on stock ensuite le channel dans un string
                Channel channel = _context.Channel.Find(channelId);

                //on envoie aux gens du groupe le message qui débute avec le nom du groupe
                await Clients.Group(groupName).SendAsync("NewMessage", "[" + channel.Title + "] " + message);

            }
            else
            {
                //si on ne précise ni de user, ni de canal, on envoie le message à tout le monde
                await Clients.All.SendAsync("NewMessage", "[Tous] " + message);
            }
        }

        private static string CreateChannelGroupName(int channelId)
        {
            return "Channel" + channelId;
        }
    }
}