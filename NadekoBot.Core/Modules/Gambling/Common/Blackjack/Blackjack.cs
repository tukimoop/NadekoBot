﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using NadekoBot.Core.Services;
using NadekoBot.Core.Services.Database.Models;
using NadekoBot.Modules.Gambling.Common;
using NLog;

namespace NadekoBot.Core.Modules.Gambling.Common.Blackjack
{
    public class Blackjack
    {
        public enum GameState
        {
            Starting,
            Playing,
            Ended
        }

        private Deck Deck { get; set; } = new QuadDeck();
        public Dealer Dealer { get; set; }

        private readonly Logger _log;

        public List<User> Players { get; set; } = new List<User>();
        public GameState State { get; set; } = GameState.Starting;
        public User CurrentUser { get; private set; }

        private TaskCompletionSource<bool> _currentUserMove;
        private readonly List<Stake> _stakes = new List<Stake>();
        private readonly ICurrencyService _cs;
        private readonly DbService _db;

        public event Func<Blackjack, Task> StateUpdated;
        public event Func<Blackjack, Task> GameEnded;

        private readonly SemaphoreSlim locker = new SemaphoreSlim(1, 1);

        public Blackjack(IUser starter, long bet, ICurrencyService cs, DbService db)
        {
            _cs = cs;
            _db = db;
            Dealer = new Dealer();
            _log = LogManager.GetCurrentClassLogger();
        }

        public void Start()
        {
            var _ = GameLoop();
        }

        public async Task GameLoop()
        {
            try
            {
                //wait for players to join
                await Task.Delay(20000);
                await locker.WaitAsync();
                try
                {
                    State = GameState.Playing;
                }
                finally
                {
                    locker.Release();
                }
                await PrintState();
                //if no users joined the game, end it
                if (!Players.Any())
                {
                    State = GameState.Ended;
                    var end = GameEnded?.Invoke(this);
                    return;
                }
                //give 1 card to the dealer and 2 to each player
                Dealer.Cards.Add(Deck.Draw());
                foreach (var usr in Players)
                {
                    usr.Cards.Add(Deck.Draw());
                    usr.Cards.Add(Deck.Draw());

                    if (usr.GetHandValue() == 21)
                        usr.State = User.UserState.Blackjack;
                }
                //go through all users and ask them what they want to do
                foreach (var usr in Players.Where(x => !x.Done))
                {
                    while (!usr.Done)
                    {
                        _log.Info($"Waiting for {usr.DiscordUser}'s move");
                        await PromptUserMove(usr);
                    }
                }
                await PrintState();
                State = GameState.Ended;
                await Task.Delay(2500);
                _log.Info("Dealer moves");
                await DealerMoves();
                await PrintState();
                var _ = GameEnded?.Invoke(this);
            }
            catch (Exception ex)
            {
                _log.Error("REPORT THE MESSAGE BELOW PLEASE");
                _log.Warn(ex);
                _log.Error("REPORT THE MESSAGE MESSAGE ABOVE PLEASE");
                State = GameState.Ended;
                var _ = GameEnded?.Invoke(this);
            }
        }

        private async Task PromptUserMove(User usr)
        {
            var pause = Task.Delay(20000); //10 seconds to decide
            CurrentUser = usr;
            _currentUserMove = new TaskCompletionSource<bool>();
            await PrintState();
            // either wait for the user to make an action and
            // if he doesn't - stand
            var finished = await Task.WhenAny(pause, _currentUserMove.Task);
            if (finished == pause)
            {
                await Stand(usr);
            }
            CurrentUser = null;
            _currentUserMove = null;
        }

        public async Task<bool> Join(IUser user, long bet)
        {
            await locker.WaitAsync();
            try
            {
                if (State != GameState.Starting)
                    return false;

                if (Players.Count >= 5)
                    return false;

                if (!await _cs.RemoveAsync(user, "BlackJack-gamble", bet, gamble: true))
                {
                    return false;
                }

                //add it to the stake, in case bot crashes or gets restarted during the game
                //funds will be refunded to the players on next startup
                using (var uow = _db.UnitOfWork)
                {
                    var s = new Stake()
                    {
                        Amount = bet,
                        UserId = user.Id,
                        Source = "BlackJack",
                    };
                    s = uow._context.Set<Stake>().Add(s).Entity;
                    _stakes.Add(s);
                    uow.Complete();
                }

                Players.Add(new User(user, bet));
                var _ = PrintState();
                return true;
            }
            finally
            {
                locker.Release();
            }
        }

        public async Task<bool> Stand(IUser u)
        {
            await locker.WaitAsync();
            try {
                if (CurrentUser.DiscordUser == u)
                    return await Stand(CurrentUser);

                return false;
            }
            finally
            {
                locker.Release();
            }
        }

        public async Task<bool> Stand(User u)
        {
            await locker.WaitAsync();
            try {
                if (State != GameState.Playing)
                    return false;

                if (CurrentUser != u)
                    return false;

                u.State = User.UserState.Stand;
                _currentUserMove.TrySetResult(true);
                return true;
            }
            finally
            {
                locker.Release();
            }
        }

        private async Task DealerMoves()
        {
            var hw = Dealer.GetHandValue();
            while (hw < 17
                || (hw == 17 && Dealer.Cards.Count(x => x.Number == 1) > (Dealer.GetRawHandValue() - 17) / 10))// hit on soft 17
            {
                /* Dealer has
                     A 6
                     That's 17, soft
                     hw == 17 => true
                     number of aces = 1
                     1 > 17-17 /10 => true
                    
                     AA 5
                     That's 17, again soft, since one ace is worth 11, even though another one is 1
                     hw == 17 => true
                     number of aces = 2
                     2 > 27 - 17 / 10 => true

                     AA Q 5
                     That's 17, but not soft, since both aces are worth 1
                     hw == 17 => true
                     number of aces = 2
                     2 > 37 - 17 / 10 => false
                 * */
                Dealer.Cards.Add(Deck.Draw());
                hw = Dealer.GetHandValue();
            }

            if (hw > 21)
            {
                foreach (var usr in Players)
                {
                    if (usr.State == User.UserState.Stand || usr.State == User.UserState.Blackjack)
                        usr.State = User.UserState.Won;
                    else
                        usr.State = User.UserState.Lost;
                }
            }
            else
            {
                foreach (var usr in Players)
                {
                    if (usr.State == User.UserState.Blackjack)
                        usr.State = User.UserState.Won;
                    else if (usr.State == User.UserState.Stand)
                        usr.State = hw < usr.GetHandValue()
                            ? User.UserState.Won
                            : User.UserState.Lost;
                    else
                        usr.State = User.UserState.Lost;
                }
            }
            using (var uow = _db.UnitOfWork)
            {
                uow._context.Set<Stake>().RemoveRange(_stakes);
                foreach (var usr in Players)
                {
                    if (usr.State == User.UserState.Won || usr.State == User.UserState.Blackjack)
                    {
                        await _cs.AddAsync(usr.DiscordUser.Id, "BlackJack-win", usr.Bet * 2, gamble: true);
                    }
                }
                uow.Complete();
            }
        }

        public async Task<bool> Double(IUser u)
        {
            await locker.WaitAsync();
            try
            {
                if (CurrentUser.DiscordUser == u)
                    return await Double(CurrentUser);

                return false;
            }
            finally
            {
                locker.Release();
            }
        }

        public async Task<bool> Double(User u)
        {
            await locker.WaitAsync();
            try
            {
                if (State != GameState.Playing)
                    return false;

                if (CurrentUser != u)
                    return false;

                if (!await _cs.RemoveAsync(u.DiscordUser.Id, "Blackjack-double", u.Bet))
                    return false;

                //read up in Join() why this is done
                using (var uow = _db.UnitOfWork)
                {
                    var s = new Stake()
                    {
                        Amount = u.Bet,
                        UserId = u.DiscordUser.Id,
                        Source = "BlackJack",
                    };
                    s = uow._context.Set<Stake>().Add(s).Entity;
                    _stakes.Add(s);
                    uow.Complete();
                }

                u.Bet *= 2;

                u.Cards.Add(Deck.Draw());

                if (u.GetHandValue() == 21)
                {
                    //blackjack
                    u.State = User.UserState.Blackjack;
                }
                else if (u.GetHandValue() > 21)
                {
                    // user busted
                    u.State = User.UserState.Bust;
                }
                else
                {
                    //with double you just get one card, and then you're done
                    u.State = User.UserState.Stand;
                }
                _currentUserMove.TrySetResult(true);

                return true;
            }
            finally
            {
                locker.Release();
            }
        }

        public async Task<bool> Hit(IUser u)
        {
            await locker.WaitAsync();
            try
            {
                if (CurrentUser.DiscordUser == u)
                    return await Hit(CurrentUser);

                return false;
            }
            finally
            {
                locker.Release();
            }
        }


        public async Task<bool> Hit(User u)
        {
            await locker.WaitAsync();
            try
            {
                if (State != GameState.Playing)
                    return false;

                if (CurrentUser != u)
                    return false;


                u.Cards.Add(Deck.Draw());

                if (u.GetHandValue() == 21)
                {
                    //blackjack
                    u.State = User.UserState.Blackjack;
                }
                else if (u.GetHandValue() > 21)
                {
                    // user busted
                    u.State = User.UserState.Bust;
                }
                else
                {
                    //you can hit or stand again
                }
                _currentUserMove.TrySetResult(true);

                return true;
            }
            finally
            {
                locker.Release();
            }
        }

        public Task PrintState()
        {
            if (StateUpdated == null)
                return Task.CompletedTask;
            return StateUpdated.Invoke(this);
        }
    }
}